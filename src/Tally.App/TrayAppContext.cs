using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tally.Capture;
using Tally.Core;

namespace Tally.App;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly DbContextOptions<TallyDbContext> _dbOptions;
    private readonly TallySettings _settings;
    private readonly string _reportsDirectory;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _liveIcon;
    private readonly System.Drawing.Icon _pausedIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly EventRecorder _recorder;
    private readonly ForegroundWatcher _foreground;
    private readonly IdleWatcher _idle;
    private readonly SessionWatcher _session;
    private readonly MicWatcher _mic;
    private readonly System.Windows.Forms.Timer _autoReportTimer;
    private IReadOnlyList<TimeOnly> _autoReportTimes;
    private HashSet<TimeOnly> _firedTimes = [];
    private DateOnly _firedDate;
    private LiveWindow? _liveWindow;
    private readonly ManualTimerService _timerService;
    private readonly HotkeyListener _hotkeys;
    private readonly TimerBubble _bubble;
    private readonly ToolStripMenuItem _timerMenuItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private bool _firstUpdateCheck = true;

    public TrayAppContext()
    {
        TallyPaths.EnsureCreated();
        if (!File.Exists(TallyPaths.RulesPath))
            RulesFile.WriteDefault(TallyPaths.RulesPath);
        _settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
        _reportsDirectory = _settings.ResolveReportsDirectory();
        Autostart.Apply(_settings.AutoStart);

        _dbOptions = TallyDbContext.BuildOptions(TallyPaths.DatabasePath);
        using (var db = new TallyDbContext(_dbOptions))
            TallyDbContext.EnsureSchema(db);

        _recorder = new EventRecorder(_dbOptions);

        // First event of the run: marks where the watchers' knowledge resumes, so a call that
        // ended while Tally was down can't be left open across everything that follows.
        _recorder.Record(new Tally.Core.Models.TrackedEvent
        {
            Timestamp = DateTimeOffset.Now,
            Kind = Tally.Core.Models.EventKind.Startup,
        });

        _timerService = new ManualTimerService(_recorder.RecordTimer);

        _foreground = new ForegroundWatcher();
        _idle = new IdleWatcher();
        _session = new SessionWatcher();
        _mic = new MicWatcher();
        _foreground.EventCaptured += _recorder.Record;
        _idle.EventCaptured += _recorder.Record;
        _session.EventCaptured += _recorder.Record;
        _mic.EventCaptured += _recorder.Record;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open live view", null, (_, _) => OpenLiveView()));
        _timerMenuItem = new ToolStripMenuItem("Start timer", null, (_, _) => _timerService.Toggle());
        menu.Items.Add(_timerMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pause tracking", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Generate today's report", null, (_, _) => GenerateReport(0)));
        menu.Items.Add(new ToolStripMenuItem("Generate yesterday's report", null, (_, _) => GenerateReport(-1)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsDialog.Configure(null, _hotkeys, ReloadAutoReportSchedule)));
        menu.Items.Add(new ToolStripMenuItem("Check for updates…", null, (_, _) => CheckForUpdates()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open reports folder", null, (_, _) => OpenFolder(_reportsDirectory)));
        menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(TallyPaths.Root)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _liveIcon = LoadTrayIcon("tally.ico");
        _pausedIcon = LoadTrayIcon("tally-paused.ico");
        _trayIcon = new NotifyIcon
        {
            Icon = _liveIcon,
            Text = "Tally — tracking",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Left-click the tray icon opens the live view (right-click still shows the menu, which
        // NotifyIcon handles on its own). Handy when Tally is closed to the tray.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenLiveView();
        };

        _hotkeys = new HotkeyListener(
            _settings.TimerStartHotkey, _settings.TimerStopHotkey,
            onStart: () => _timerService.Start(), onStop: () => _timerService.Stop());
        _bubble = new TimerBubble(_timerService);
        _bubble.StopRequested += () => _timerService.Stop();
        _bubble.RestoreRequested += OpenLiveView;
        _timerService.Changed += OnTimerChanged;

        _autoReportTimes = _settings.ResolveAutoReportTimes();
        _firedDate = DateOnly.FromDateTime(DateTime.Now);
        _autoReportTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _autoReportTimer.Tick += (_, _) => AutoReportTick();
        _autoReportTimer.Start();

        _foreground.Start();
        _idle.Start();
        _session.Start();
        _mic.Start();

        // First auto-update check ~8s after startup (once the message loop is running, so the
        // async continuation resumes on the UI thread), then every 4 hours.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 8_000 };
        _updateTimer.Tick += (_, _) => RunUpdateCheck();
        _updateTimer.Start();

        Log.Info("Tally started");
    }

    private void RunUpdateCheck()
    {
        if (_firstUpdateCheck)
        {
            _firstUpdateCheck = false;
            _updateTimer.Interval = (int)TimeSpan.FromHours(4).TotalMilliseconds;
        }

        _ = AppUpdater.CheckAndStageAsync(version =>
            _trayIcon.ShowBalloonTip(10_000, "Tally", $"Update {version} downloaded — it applies next time you restart Tally.", ToolTipIcon.Info));
    }

    // Right-click tray -> "Check for updates…": check now, and if one exists, download it and
    // restart into it right away instead of waiting for the periodic check.
    private void CheckForUpdates()
    {
        _trayIcon.ShowBalloonTip(4_000, "Tally", "Checking for updates…", ToolTipIcon.Info);
        _ = AppUpdater.CheckNowAsync(status =>
            _trayIcon.ShowBalloonTip(8_000, "Tally", status, ToolTipIcon.Info));
    }

    private static System.Drawing.Icon LoadTrayIcon(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            // The size hint picks the 16px frame instead of scaling down a larger one.
            return new System.Drawing.Icon(path, SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load tray icon {fileName} — falling back to the generic icon", ex);
            return System.Drawing.SystemIcons.Application;
        }
    }

    // async void is acceptable here: it is a UI timer handler and all awaited work is in the try/catch.
    private async void AutoReportTick()
    {
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (_firedDate != today)
            {
                _firedDate = today;
                _firedTimes.Clear();
            }

            // Any configured time that has passed today and hasn't fired yet is due. If several are
            // due at once (e.g. catching up at startup), generate ONE report and mark them all —
            // each would show the same "today so far", so duplicates add nothing.
            var nowTime = TimeOnly.FromDateTime(now);
            var due = _autoReportTimes.Where(t => t <= nowTime && !_firedTimes.Contains(t)).ToList();
            if (due.Count == 0)
                return;

            foreach (var t in due)
                _firedTimes.Add(t);

            var path = await ReportGenerator.GenerateAsync(_dbOptions, today, _reportsDirectory, _settings.ResolveReportFormat());
            Log.Info($"Automatic report generated (times {string.Join(", ", due)}): {path}");
            if (_settings.OpenReportOnAutoGenerate)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                _trayIcon.ShowBalloonTip(10_000, "Tally", "A report is ready — open it from the tray menu.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Automatic report generation failed", ex);
        }
    }

    // Re-reads the schedule after Settings changes. Times already passed today are marked fired so a
    // settings save doesn't retroactively spit out a report; new/future times still fire on arrival.
    private void ReloadAutoReportSchedule()
    {
        var settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
        _autoReportTimes = settings.ResolveAutoReportTimes();
        var now = DateTime.Now;
        _firedDate = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);
        _firedTimes = [.. _autoReportTimes.Where(t => t <= nowTime)];
    }

    private void OpenLiveView()
    {
        if (_liveWindow is null || _liveWindow.IsDisposed)
            _liveWindow = new LiveWindow(_dbOptions, _settings, _reportsDirectory, _timerService, _hotkeys, ReloadAutoReportSchedule);

        _liveWindow.ShowLive();
        UpdateBubble();
    }

    private void OnTimerChanged()
    {
        _timerMenuItem.Text = _timerService.IsActive
            ? $"Stop timer: {Shorten(_timerService.Active!.Name)}"
            : "Start timer";
        UpdateTrayText();
        UpdateBubble();
    }

    // The floating bubble shows whenever a manual timer is running — including while the live
    // window is open — so the running timer is always visible and draggable on screen.
    private void UpdateBubble()
    {
        if (_timerService.IsActive)
            _bubble.ShowBubble();
        else
            _bubble.HideBubble();
    }

    private void UpdateTrayText()
        => _trayIcon.Text = _timerService.IsActive
            ? $"Tally — timer: {Shorten(_timerService.Active!.Name)}"
            : (_recorder.Paused ? "Tally — paused" : "Tally — tracking");

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..39] + "…";

    private void TogglePause()
    {
        _recorder.Paused = !_recorder.Paused;
        _pauseItem.Text = _recorder.Paused ? "Resume tracking" : "Pause tracking";
        _trayIcon.Icon = _recorder.Paused ? _pausedIcon : _liveIcon;   // red when paused, green when live
        UpdateTrayText();
    }

    // async void is acceptable here: it is a UI event handler and all awaited work is wrapped in try/catch.
    private async void GenerateReport(int dayOffset)
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now.AddDays(dayOffset));
            var path = await ReportGenerator.GenerateAsync(_dbOptions, date, _reportsDirectory, _settings.ResolveReportFormat());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Report generation failed", ex);
            _trayIcon.ShowBalloonTip(5000, "Tally", "Report generation failed — see logs.", ToolTipIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open folder {path}", ex);
        }
    }

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _updateTimer.Dispose();
        _hotkeys.Dispose();
        _bubble.Dispose();
        _liveWindow?.Dispose();
        _autoReportTimer.Dispose();
        _foreground.Dispose();
        _idle.Dispose();
        _session.Dispose();
        _mic.Dispose();

        // Watchers are stopped, so the channel drains and completes quickly; the writer runs on
        // the thread pool with no UI synchronization context to deadlock against.
        _recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _trayIcon.Dispose();
        _liveIcon.Dispose();
        _pausedIcon.Dispose();
        Log.Info("Tally stopped");
        ExitThread();
    }
}
