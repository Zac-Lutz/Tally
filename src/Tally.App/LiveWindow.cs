using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tally.Core;

namespace Tally.App;

/// <summary>
/// Live dashboard: a WebView2 hosting the same report rendering, refreshed in place every few
/// seconds so the current day's rollup/timeline/calls/activity update without generating a file.
/// A snapshot report is still one toolbar click away.
/// </summary>
public sealed class LiveWindow : Form
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DbContextOptions<TallyDbContext> _dbOptions;
    private readonly TallySettings _settings;
    private readonly string _reportsDirectory;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = (int)RefreshInterval.TotalMilliseconds };
    private readonly ToolStripLabel _statusLabel = new("Starting…");
    private bool _ready;
    private bool _refreshing;

    /// <summary>When true (tray-hosted), closing hides the window to keep WebView2 warm. Standalone
    /// (`--live`) sets this false so closing exits the process. A field, not a property, to avoid
    /// the WinForms designer-serialization analyzer (WFO1000).</summary>
    internal bool HideOnClose = true;

    public LiveWindow(DbContextOptions<TallyDbContext> dbOptions, TallySettings settings, string reportsDirectory)
    {
        _dbOptions = dbOptions;
        _settings = settings;
        _reportsDirectory = reportsDirectory;

        Text = "Tally — Live";
        Width = 1120;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico"));
        }
        catch
        {
            // Non-fatal; the window just uses the default icon.
        }

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var snapshot = new ToolStripButton("Generate snapshot report") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        snapshot.Click += (_, _) => GenerateSnapshot();
        toolbar.Items.Add(snapshot);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_statusLabel);

        Controls.Add(_webView);
        Controls.Add(toolbar);

        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Load += (_, _) => InitializeWebViewAsync();
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
            _refreshTimer.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
