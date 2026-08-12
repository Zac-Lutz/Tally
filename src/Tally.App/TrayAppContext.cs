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
    private readonly ToolStripMenuItem _pauseItem;
    private readonly EventRecorder _recorder;
    private readonly ForegroundWatcher _foreground;
    private readonly IdleWatcher _idle;
    private readonly SessionWatcher _session;
    private readonly MicWatcher _mic;
    private readonly System.Windows.Forms.Timer? _autoReportTimer;
    private readonly TimeOnly? _autoReportTime;
    private DateOnly _lastAutoReportDate;

    public TrayAppContext()
    {
        TallyPaths.EnsureCreated();
        if (!File.Exists(TallyPaths.RulesPath))
            RulesFile.WriteDefault(TallyPaths.RulesPath);
        _settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
        _reportsDirectory = _settings.ResolveReportsDirectory();

        _dbOptions = TallyDbContext.BuildOptions(TallyPaths.DatabasePath);
        using (var db = new TallyDbContext(_dbOptions))
            db.Database.EnsureCreated();

        _recorder = new EventRecorder(_dbOptions);

        _foreground = new ForegroundWatcher();
        _idle = new IdleWatcher();
        _session = new SessionWatcher();
        _mic = new MicWatcher();
        _foreground.EventCaptured += _recorder.Record;
        _idle.EventCaptured += _recorder.Record;
        _session.EventCaptured += _recorder.Record;
        _mic.EventCaptured += _recorder.Record;

        var menu = new ContextMenuStrip();
        _pauseItem = new ToolStripMenuItem("Pause tracking", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Generate today's report", null, (_, _) => GenerateReport(0)));
        menu.Items.Add(new ToolStripMenuItem("Generate yesterday's report", null, (_, _) => GenerateReport(-1)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open reports folder", null, (_, _) => OpenFolder(_reportsDirectory)));
        menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(TallyPaths.Root)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Tally — tracking",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _autoReportTime = _settings.ParseAutoReportTime();
        if (_settings.AutoReportTime is not null && _autoReportTime is null)
            Log.Error($"settings.json autoReportTime '{_settings.AutoReportTime}' is not HH:mm — automatic report disabled");
        if (_autoReportTime is not null)
        {
            _autoReportTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            _autoReportTimer.Tick += (_, _) => AutoReportTick();
            _autoReportTimer.Start();
        }

        _foreground.Start();
        _idle.Start();
        _session.Start();
        _mic.Start();
        Log.Info("Tally started");
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico");
            // The size hint picks the 16px frame instead of scaling down a larger one.
            return new System.Drawing.Icon(path, SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load tray icon — falling back to the generic icon", ex);
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
            if (_lastAutoReportDate == today || TimeOnly.FromDateTime(now) < _autoReportTime!.Value)
                return;

            _lastAutoReportDate = today;
            var path = await ReportGenerator.GenerateAsync(_dbOptions, today, _reportsDirectory);
            Log.Info($"Automatic daily report generated: {path}");
            if (_settings.OpenReportOnAutoGenerate)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                _trayIcon.ShowBalloonTip(10_000, "Tally", "Today's report is ready — open it from the tray menu.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Automatic report generation failed", ex);
        }
    }

    private void TogglePause()
    {
        _recorder.Paused = !_recorder.Paused;
        _pauseItem.Text = _recorder.Paused ? "Resume tracking" : "Pause tracking";
        _trayIcon.Text = _recorder.Paused ? "Tally — paused" : "Tally — tracking";
    }

    // async void is acceptable here: it is a UI event handler and all awaited work is wrapped in try/catch.
    private async void GenerateReport(int dayOffset)
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now.AddDays(dayOffset));
            var path = await ReportGenerator.GenerateAsync(_dbOptions, date, _reportsDirectory);
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
        _autoReportTimer?.Dispose();
        _foreground.Dispose();
        _idle.Dispose();
        _session.Dispose();
        _mic.Dispose();

        // Watchers are stopped, so the channel drains and completes quickly; the writer runs on
        // the thread pool with no UI synchronization context to deadlock against.
        _recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _trayIcon.Dispose();
        Log.Info("Tally stopped");
        ExitThread();
    }
}
