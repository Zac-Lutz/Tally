using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tally.Core;

namespace Tally.App;

/// <summary>
/// Live dashboard: a WebView2 hosting the same report rendering, refreshed in place every few
/// seconds so the current day's rollup/timeline/calls/activity update without generating a file.
/// A snapshot report is still one toolbar click away. Dark-themed to match the report content.
/// </summary>
public sealed class LiveWindow : Form
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    // Dark palette matching the report's dark theme.
    private static readonly Color ChromeBg = Color.FromArgb(0x16, 0x18, 0x1c);
    private static readonly Color InputBg = Color.FromArgb(0x2a, 0x2e, 0x35);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color AccentFg = Color.FromArgb(0x08, 0x20, 0x1c);
    private static readonly Color StopColor = Color.FromArgb(0xe0, 0x52, 0x52);
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe9, 0xec);
    private static readonly Color MutedFg = Color.FromArgb(0x9a, 0xa4, 0xae);

    private readonly DbContextOptions<TallyDbContext> _dbOptions;
    private readonly TallySettings _settings;
    private readonly string _reportsDirectory;
    private readonly ManualTimerService _timer;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = ChromeBg };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = (int)RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _timerTick = new() { Interval = 1000 };
    private readonly Label _statusLabel = new() { Text = "Starting…", AutoSize = true, ForeColor = MutedFg, Margin = new Padding(0, 10, 0, 0) };
    private readonly TextBox _timerName = new() { Width = 220, BackColor = InputBg, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(8, 6, 8, 0) };
    private readonly Button _timerButton = new() { AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 12, 0), Padding = new Padding(8, 3, 8, 3), Cursor = Cursors.Hand };
    private readonly Label _timerElapsed = new() { AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), Margin = new Padding(0, 7, 0, 0) };
    private bool _ready;
    private bool _refreshing;
    private bool _syncingTimerUi;

    /// <summary>Raised when the window is shown, hidden, minimized, or restored (drives the bubble).</summary>
    public event Action? VisibilityChanged;

    /// <summary>True when the window is visible and not minimized.</summary>
    public bool IsShownNormally => Visible && WindowState != FormWindowState.Minimized;

    /// <summary>When true (tray-hosted), closing hides the window to keep WebView2 warm. Standalone
    /// (`--live`) sets this false so closing exits the process. A field, not a property, to avoid
    /// the WinForms designer-serialization analyzer (WFO1000).</summary>
    internal bool HideOnClose = true;

    public LiveWindow(DbContextOptions<TallyDbContext> dbOptions, TallySettings settings, string reportsDirectory, ManualTimerService timer)
    {
        _dbOptions = dbOptions;
        _settings = settings;
        _reportsDirectory = reportsDirectory;
        _timer = timer;

        Text = "Tally — Live";
        Width = 1120;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ChromeBg;
        try
        {
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico"));
        }
        catch
        {
            // Non-fatal; the window just uses the default icon.
        }

        var snapshot = new Button
        {
            Text = "Generate snapshot report",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = AccentFg,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 0, 12, 0),
            Cursor = Cursors.Hand,
        };
        snapshot.FlatAppearance.BorderSize = 0;
        snapshot.Click += (_, _) => GenerateSnapshot();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = ChromeBg,
            Padding = new Padding(10, 8, 10, 8),
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        bar.Controls.Add(snapshot);
        bar.Controls.Add(_statusLabel);

        var timerBar = BuildTimerBar();

        // Docked tops stack by add order: last-added claims the topmost edge.
        Controls.Add(_webView);   // Fill
        Controls.Add(bar);        // toolbar (below the timer bar)
        Controls.Add(timerBar);   // timer bar on top

        _timer.Changed += SyncTimerUi;
        _timerTick.Tick += (_, _) => UpdateElapsed();
        SyncTimerUi();

        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Load += (_, _) => InitializeWebViewAsync();
    }

    private FlowLayoutPanel BuildTimerBar()
    {
        _timerName.GotFocus += (_, _) => { };   // (kept simple; focus state read in SyncTimerUi)
        _timerName.TextChanged += (_, _) =>
        {
            if (!_syncingTimerUi)
                _timer.Rename(_timerName.Text);
        };
        _timerButton.Click += (_, _) =>
        {
            if (_timer.IsActive)
                _timer.Stop();
            else
                _timer.Start(_timerName.Text);
        };

        var label = new Label
        {
            Text = "TIMER",
            AutoSize = true,
            ForeColor = MutedFg,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Margin = new Padding(0, 9, 8, 0),
        };

        var timerBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = ChromeBg,
            Padding = new Padding(10, 6, 10, 6),
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        timerBar.Controls.Add(label);
        timerBar.Controls.Add(_timerName);
        timerBar.Controls.Add(_timerButton);
        timerBar.Controls.Add(_timerElapsed);
        return timerBar;
    }

    private void SyncTimerUi()
    {
        if (IsDisposed)
            return;

        _syncingTimerUi = true;
        var active = _timer.IsActive;
        _timerButton.Text = active ? "Stop" : "Start";
        _timerButton.BackColor = active ? StopColor : Accent;
        _timerButton.ForeColor = active ? Color.White : AccentFg;
        _timerButton.FlatAppearance.BorderSize = 0;

        // Don't fight the user mid-type; otherwise reflect the authoritative name.
        if (!_timerName.Focused)
            _timerName.Text = active ? _timer.Active!.Name : _timer.PendingName;
        _syncingTimerUi = false;

        UpdateElapsed();
        if (active)
            _timerTick.Start();
        else
            _timerTick.Stop();
    }

    private void UpdateElapsed()
        => _timerElapsed.Text = _timer.IsActive ? TimerText.Elapsed(_timer.Elapsed) : string.Empty;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        VisibilityChanged?.Invoke();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        VisibilityChanged?.Invoke();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }

    // async void: WinForms event handler; all awaited work is wrapped in try/catch.
    private async void InitializeWebViewAsync()
    {
        try
        {
            // Keep WebView2's data outside %LocalAppData% (MSIX-safe) and beside the rest of Tally.
            var userDataFolder = Path.Combine(TallyPaths.Root, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            // Force the page's theme to dark regardless of the OS setting, so the live view is
            // always dark to match the window chrome.
            try
            {
                _webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            }
            catch (NotImplementedException)
            {
                // Older runtime without profile support — content still follows the OS theme.
            }

            _webView.CoreWebView2.NavigateToString(HtmlReportWriter.BuildLiveShell());
            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                _ready = true;
                await RefreshAsync();
                _refreshTimer.Start();
            };
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 failed to initialize for the live view", ex);
            _statusLabel.Text = "WebView2 runtime not available — install the Microsoft Edge WebView2 Runtime.";
        }
    }

    private async Task RefreshAsync()
    {
        if (!_ready || _refreshing)
            return;

        _refreshing = true;
        try
        {
            var data = await ReportGenerator.ComputeAsync(_dbOptions, DateOnly.FromDateTime(DateTime.Now));
            var inner = HtmlReportWriter.BuildMainInner(data.Date, data.Blocks, data.Calls, data.Inactive);
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.tallyUpdate({JsonSerializer.Serialize(inner)})");
            _statusLabel.Text = $"Live · updated {DateTime.Now:h:mm:ss tt}";
        }
        catch (Exception ex)
        {
            Log.Error("Live view refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void GenerateSnapshot()
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            var path = await ReportGenerator.GenerateAsync(_dbOptions, date, _reportsDirectory, _settings.ResolveReportFormat());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Snapshot report generation failed from the live view", ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing from the window chrome hides it (keeps WebView2 warm for the next open); the app
        // disposes it for real on exit.
        if (HideOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _refreshTimer.Stop();
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>Shows the window (creating nothing) and resumes live refresh.</summary>
    public void ShowLive()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (_ready)
        {
            _refreshTimer.Start();
            _ = RefreshAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Changed -= SyncTimerUi;
            _timerTick.Dispose();
            _refreshTimer.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Enables the Windows 11 dark title bar for a window via DWM.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        var enabled = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+/Win11); 19 = the pre-20H1 attribute id.
        if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }
}
