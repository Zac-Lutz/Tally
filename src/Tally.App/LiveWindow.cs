using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tally.Core;
using Tally.Core.Models;

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
    private readonly HotkeyListener? _hotkeys;
    private readonly Action? _onSettingsSaved;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = ChromeBg };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = (int)RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _timerTick = new() { Interval = 1000 };
    private readonly Label _titleLabel = new() { Text = "Tally", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold), Margin = new Padding(0, 5, 4, 0) };
    private readonly Label _versionLabel = new() { Text = "", AutoSize = true, ForeColor = MutedFg, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 13, 18, 0) };
    private readonly Label _statusLabel = new() { Text = "Starting…", AutoSize = true, ForeColor = MutedFg, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(12, 11, 0, 0) };
    private readonly Label _timerElapsed = new() { AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), Margin = new Padding(0, 4, 14, 0) };
    private readonly Button _prevDay = ArrowButton("❮", "Previous day");
    private readonly Button _dateButton = ChromeButton("Today", "Pick a day");
    private readonly Button _nextDay = ArrowButton("❯", "Next day");
    private readonly Button _todayButton = ChromeButton("Today", "Back to today");
    private bool _ready;
    private bool _refreshing;
    private string? _note;
    private DateTime _noteUntil;
    private string? _pendingTab;

    /// <summary>
    /// Whether the window is tracking the current day rather than a day the user picked. Tracking
    /// is a rule, not a stored date, so a window left open past midnight rolls over on its own —
    /// which is what it always did before there was anywhere else to go.
    /// </summary>
    private bool _followToday = true;
    private DateOnly _pinnedDate = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The first day still on record, so the back arrow stops where the data does. Read
    /// from the database on the first refresh and after each move, since retention purges the
    /// oldest days as the app runs.</summary>
    private DateOnly _earliest = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _earliestReadOn = DateOnly.MinValue;

    /// <summary>The day every tab, edit, export and snapshot in this window is about.</summary>
    private DateOnly ViewDate => _followToday ? DateOnly.FromDateTime(DateTime.Now) : _pinnedDate;

    /// <summary>How long a saved-edit note stays beside the live status before it fades out.</summary>
    private const int NoteDuration = 8;

    /// <summary>When true (tray-hosted), closing hides the window to keep WebView2 warm. Standalone
    /// (`--live`) sets this false so closing exits the process. A field, not a property, to avoid
    /// the WinForms designer-serialization analyzer (WFO1000).</summary>
    internal bool HideOnClose = true;

    public LiveWindow(DbContextOptions<TallyDbContext> dbOptions, TallySettings settings, string reportsDirectory, ManualTimerService timer, HotkeyListener? hotkeys = null, Action? onSettingsSaved = null)
    {
        _dbOptions = dbOptions;
        _settings = settings;
        _reportsDirectory = reportsDirectory;
        _timer = timer;
        _hotkeys = hotkeys;
        _onSettingsSaved = onSettingsSaved;

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

        Controls.Add(_webView);        // Fill
        Controls.Add(BuildTopBar());   // top bar claims the top edge

        _timer.Changed += SyncTimerUi;
        _timerTick.Tick += (_, _) => UpdateElapsed();
        SyncTimerUi();

        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Load += (_, _) => InitializeWebViewAsync();
    }

    // One top bar: "Tally", the day picker, and the live-updated status on the LEFT; the running
    // timer's elapsed time, then export/snapshot on the RIGHT. Starting and naming a timer lives in
    // the Timers tab; only the elapsed figure stays up here, so a running timer is visible from
    // whichever tab you're on. The day picker sits in the chrome rather than in a tab because it
    // re-frames the whole window, tabs included — every tab below is about the day named up here.
    private Panel BuildTopBar()
    {
        _prevDay.Click += (_, _) => StepDay(-1);
        _nextDay.Click += (_, _) => StepDay(+1);
        _dateButton.Click += (_, _) => ShowDatePicker();
        _todayButton.Click += (_, _) => GoTo(null);
        _todayButton.Visible = false;   // nothing to return to until the day is moved

        var snapshot = new Button { Text = "Generate snapshot", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 1, 8, 0), Cursor = Cursors.Hand };
        StyleButton(snapshot);
        snapshot.Click += (_, _) => GenerateSnapshot();

        var export = new Button { Text = "Export timesheet", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 1, 8, 0), Cursor = Cursors.Hand };
        StyleButton(export);
        export.Click += (_, _) => ExportTimesheet();

        // "Tally" (in the accent color) with the running version to its right, so the current
        // version is always visible at a glance (e.g. "v1.2.3", or "dev" for a from-source build).
        _versionLabel.Text = AppUpdater.DisplayVersion;

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ChromeBg,
            Padding = new Padding(14, 10, 0, 0),
        };
        left.Controls.Add(_titleLabel);
        left.Controls.Add(_versionLabel);
        left.Controls.Add(_prevDay);
        left.Controls.Add(_dateButton);
        left.Controls.Add(_nextDay);
        left.Controls.Add(_todayButton);
        left.Controls.Add(_statusLabel);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ChromeBg,
            Padding = new Padding(0, 10, 14, 0),
        };
        right.Controls.Add(_timerElapsed);
        right.Controls.Add(export);
        right.Controls.Add(snapshot);

        var bar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = ChromeBg };
        bar.Controls.Add(left);
        bar.Controls.Add(right);
        return bar;
    }

    // Uniform dark buttons that light up in the accent color on hover (dark text for contrast).
    // The resting foreground is stashed in Tag so MouseLeave can restore it (the timer button
    // changes its resting color with state).
    private static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;   // honor BackColor + FlatAppearance, not the theme
        b.BackColor = InputBg;
        b.ForeColor = Fg;
        b.Tag = Fg;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Accent;
        b.FlatAppearance.MouseDownBackColor = Accent;
        b.MouseEnter += (_, _) => b.ForeColor = AccentFg;
        b.MouseLeave += (_, _) => b.ForeColor = b.Tag is Color c ? c : Fg;
    }

    /// <summary>A top-bar button in the window's own styling.</summary>
    private static Button ChromeButton(string text, string description)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 1, 6, 0),
            Cursor = Cursors.Hand,
            AccessibleName = description,
        };
        StyleButton(b);
        return b;
    }

    /// <summary>
    /// A day arrow. Sized explicitly because an auto-sized WinForms button never goes below the
    /// stock 75px, which would leave a chevron floating in a button five times its width; the
    /// description is the accessible name, since a glyph has no word in it for a screen reader.
    /// </summary>
    private static Button ArrowButton(string glyph, string description)
    {
        var b = new Button
        {
            Text = glyph,
            AutoSize = false,
            Size = new Size(32, 27),
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(0, 1, 4, 0),
            Cursor = Cursors.Hand,
            AccessibleName = description,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        StyleButton(b);
        return b;
    }

    // ---- Which day the window is showing ----------------------------------------------------

    /// <summary>Steps a day at a time, stopping at today and at the first day still on record.</summary>
    private void StepDay(int days) => GoTo(DayNavigation.Step(ViewDate, days, _earliest, DateOnly.FromDateTime(DateTime.Now)));

    /// <summary>
    /// Shows <paramref name="date"/> — or returns to following the current day when it is null,
    /// which is what the Today button and a fresh window do. Everything the window can act on
    /// (edits, export, snapshot) reads <see cref="ViewDate"/>, so this one call moves all of it.
    /// </summary>
    private void GoTo(DateOnly? date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (date is { } picked && picked != today)
        {
            _followToday = false;
            _pinnedDate = DayNavigation.Clamp(picked, _earliest, today);
        }
        else
        {
            _followToday = true;
        }

        // Moving is the moment to re-check how far back the data goes.
        _earliestReadOn = DateOnly.MinValue;

        // A finished day can't change, so the periodic refresh is switched off while one is shown:
        // the only thing that moves it is an edit, and every edit already asks for a refresh.
        if (_followToday)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();

        UpdateNavChrome();
        _ = RefreshAsync();
    }

    /// <summary>The arrows, the date, and the window title, brought in line with the day shown.</summary>
    private void UpdateNavChrome()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var date = ViewDate;
        _dateButton.Text = DayNavigation.Label(date, today);
        _prevDay.Enabled = DayNavigation.CanGoBack(date, _earliest, today);
        _nextDay.Enabled = DayNavigation.CanGoForward(date, today);
        _todayButton.Visible = !_followToday;
        Text = _followToday ? "Tally — Live" : $"Tally — {DayNavigation.Label(date, today)}";
    }

    /// <summary>The calendar behind the date button, in the window's own colours.</summary>
    private void ShowDatePicker()
        => DayPicker.Show(
            _dateButton, ViewDate, _earliest, DateOnly.FromDateTime(DateTime.Now),
            new DayPickerTheme(ChromeBg, InputBg, Fg, MutedFg, Accent, AccentFg),
            date => GoTo(date));

    /// <summary>
    /// The first day the database can still rebuild — the earliest recorded event or timer. Both
    /// columns are indexed, so this is a seek rather than a scan; it is re-read on each move so a
    /// retention purge that runs while the window is open shortens the range rather than offering
    /// days whose events have gone.
    /// </summary>
    private async Task<DateOnly> EarliestRecordedDayAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            await using var db = new TallyDbContext(_dbOptions);
            TallyDbContext.EnsureSchema(db);
            var firstEvent = await db.Events.AsNoTracking()
                .OrderBy(e => e.Timestamp).Select(e => (DateTimeOffset?)e.Timestamp).FirstOrDefaultAsync();
            var firstTimer = await db.ManualTimers.AsNoTracking()
                .OrderBy(t => t.Start).Select(t => (DateTimeOffset?)t.Start).FirstOrDefaultAsync();

            var first = (firstEvent, firstTimer) switch
            {
                ({ } e, { } t) => e < t ? e : t,
                ({ } e, null) => e,
                (null, { } t) => t,
                _ => (DateTimeOffset?)null,
            };
            // Nothing recorded yet reads as today: a range of one day, and both arrows off.
            return first is { } start ? DateOnly.FromDateTime(start.ToLocalTime().DateTime) : today;
        }
        catch (Exception ex)
        {
            // Unknown rather than empty: opening the range wide shows some empty days, where
            // closing it would hide real ones.
            Log.Error("Couldn't read the first recorded day — allowing the last five years", ex);
            return today.AddYears(-5);
        }
    }

    // The timer changed — from the Timers tab, a hotkey, the tray, or the bubble. The top bar's
    // elapsed figure updates here; the tab's control is HTML, so it comes back with a refresh.
    private void SyncTimerUi()
    {
        if (IsDisposed)
            return;

        UpdateElapsed();
        if (_timer.IsActive)
            _timerTick.Start();
        else
            _timerTick.Stop();

        _ = RefreshAsync();
    }

    private void UpdateElapsed()
        => _timerElapsed.Text = _timer.IsActive ? TimerText.Elapsed(_timer.Elapsed) : string.Empty;

    /// <summary>The Timers tab's control state: the running timer, or the name waiting on Start.</summary>
    private TimerPanelState TimerPanel()
        => _timer.Active is { } active
            ? new TimerPanelState(active.Name, active.StartedAt, _timer.Elapsed)
            : new TimerPanelState(_timer.PendingName, null);

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

            // Receives edits posted from the live view: ticket overrides and timer renames.
            _webView.CoreWebView2.WebMessageReceived += OnEditWebMessage;

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
            // How far back the arrows may go moves as days are recorded and as retention purges the
            // oldest — but not between two ticks five seconds apart. Re-read once a day, and
            // whenever the user moves, rather than on every refresh.
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_earliestReadOn != today)
            {
                _earliest = await EarliestRecordedDayAsync();
                _earliestReadOn = today;
            }

            var date = ViewDate;
            var data = await ReportGenerator.ComputeAsync(_dbOptions, date);
            var categories = ReportGenerator.LoadCategoriesSafe();

            // Settings are re-read per refresh so the tab always shows the file's truth (a save
            // from this same tab lands on the very next tick).
            var current = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
            var settingsPanel = new SettingsPanelState(
                current.TimerStartHotkey, current.TimerStopHotkey,
                current.ResolveAutoReportTimes().Select(t => t.ToString("HH:mm")).ToList(),
                current.ResolveEventRetentionDays());

            // The stopwatch belongs to now, so it is only offered on today: a Start button on a
            // finished day would record against a different day than the one being read. Claiming
            // past time, which does land on the day shown, stays available either way.
            var inner = HtmlReportWriter.BuildMainInner(data.Date, data.Blocks, data.Calls, data.Inactive,
                timers: data.Timers, ticketOverrides: data.TicketOverrides,
                timerPanel: date == today ? TimerPanel() : null,
                rules: LoadRulesSafe(), categories: categories, palette: new CategoryPalette(categories),
                settings: settingsPanel);
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.tallyUpdate({JsonSerializer.Serialize(inner)})");

            if (_pendingTab is { } tab)
            {
                _pendingTab = null;
                await _webView.CoreWebView2.ExecuteScriptAsync(
                    $"window.tallyShowTab && window.tallyShowTab({JsonSerializer.Serialize(tab)})");
            }
            UpdateNavChrome();
            var note = DateTime.Now < _noteUntil ? $" · {_note}" : null;
            // A finished day says so instead of claiming to be live: nothing new can arrive in it,
            // and a clock ticking beside it would suggest otherwise.
            _statusLabel.Text = date == today
                ? $"Live · updated {DateTime.Now:h:mm:ss tt}{note}"
                : $"Not live · a finished day{note}";
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

    // An edit was committed in the live view: a Rollup ticket cell (save as today's override) or a
    // Timers-tab name (rename the recorded timer in the DB). Either way, refresh so it reflects.
    private void OnEditWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<EditMessage>(e.WebMessageAsJson, EditMessageOptions);
            if (msg is null)
                return;

            if (msg.Type == "ticket" && !string.IsNullOrEmpty(msg.Key))
            {
                var rowKey = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(msg.Key));
                TicketOverrideStore.Set(ViewDate, rowKey, msg.Value);
                _ = RefreshAsync();
            }
            else if (msg.Type == "timerName" && long.TryParse(msg.Id, out var timerId))
            {
                _ = RenameTimerAsync(timerId, msg.Value);
            }
            else if (msg.Type == "timerDelete" && long.TryParse(msg.Id, out var deleteId))
            {
                _ = DeleteTimerAsync(deleteId);
            }
            else if (msg.Type == "rule")
            {
                SaveRule(msg);
            }
            else if (msg.Type == "ruleUpdate")
            {
                UpdateRule(msg);
            }
            else if (msg.Type == "ruleDelete")
            {
                DeleteRule(msg);
            }
            else if (msg.Type == "catAdd")
            {
                AddCategory(msg);
            }
            else if (msg.Type == "catColor")
            {
                SetCategoryColor(msg);
            }
            else if (msg.Type == "catRename")
            {
                RenameCategory(msg);
            }
            else if (msg.Type == "catDelete")
            {
                DeleteCategory(msg);
            }
            else if (msg.Type == "ruleAdd")
            {
                AddRule(msg);
            }
            else if (msg.Type == "settingsSave")
            {
                SaveSettings(msg);
            }
            else if (msg.Type == "timerAdd")
            {
                _ = AddPastTimerAsync(msg);
            }
            else if (msg.Type == "timerToggle")
            {
                // Start takes the name straight from the field; Stop ignores it, since the running
                // timer's name was already applied by the rename that any edit posts.
                if (_timer.IsActive)
                    _timer.Stop();
                else
                    _timer.Start(msg.Value ?? string.Empty);
            }
            else if (msg.Type == "timerRename")
            {
                _timer.Rename(msg.Value ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to apply an edit from the live view", ex);
        }
    }

    // "Save rule" from the Unclassified tab: draft a rule for that activity and add it to rules.json.
    // Rules are re-read on every recompute, so the refresh right after already shows the day
    // reclassified — the row leaves the triage list and joins the Rollup under its new category.
    private void SaveRule(EditMessage msg)
    {
        var process = Decode(msg.Process) ?? string.Empty;
        var title = Decode(msg.Title) ?? string.Empty;
        // Excluding something is a complete decision without naming it, so the category is
        // optional there and defaults to the one word that describes what was decided.
        var excludeFrom = ScopeOf(msg);
        var category = msg.Category?.Trim() is { Length: > 0 } typed
            ? typed
            : excludeFrom is not ExcludeScope.None ? ExcludedCategory : null;
        // A site rule keys on the page alone, so it needs one but needs neither of the other two.
        var url = Decode(msg.Url);
        var forSite = string.Equals(msg.Scope, "site", StringComparison.OrdinalIgnoreCase);
        if (category is null || (forSite ? url is null : process.Length == 0 && title.Length == 0))
            return;

        try
        {
            var match = msg.Scope?.ToLowerInvariant() switch
            {
                "window" => RuleMatch.Window,
                "site" => RuleMatch.Site,
                _ => RuleMatch.App,
            };
            var rule = RuleDraft.Create(
                process, title, match, category, ExistingRuleIds(), excludeFrom, url);
            // A site rule has to land below the rules that name one particular window, or it
            // outranks them: "any Halo page" above "the Halo window with a ticket number in it"
            // silently stops the ticket numbers coming through.
            RulesFile.AddRule(
                TallyPaths.RulesPath,
                rule,
                match is RuleMatch.Site ? RulePlacement.Site : RulePlacement.Specific);
            Log.Info($"Saved classification rule '{rule.Id}' ({category}{ExcludeSuffix(excludeFrom)}) from the live view");
            Note($"rule saved — {category}{ExcludeSuffix(excludeFrom)}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save a classification rule from the live view", ex);
            Note("couldn't save that rule — see the log");
        }
    }

    /// <summary>The category an excluding rule takes when the user didn't name one.</summary>
    private const string ExcludedCategory = "Excluded";

    // The page sends the scope as the word the dropdown carries ("rollup", "timesheet", "all");
    // anything else — including the empty value of "Counted" — means the rule excludes nothing.
    private static ExcludeScope ScopeOf(EditMessage msg)
        => Enum.TryParse<ExcludeScope>(msg.ExcludeFrom, ignoreCase: true, out var scope)
            ? scope
            : ExcludeScope.None;

    // What the status note and the log add about an exclusion, so a saved rule says out loud
    // which account of the day it just left.
    private static string ExcludeSuffix(ExcludeScope scope) => scope switch
    {
        ExcludeScope.Rollup => ", not in the Rollup",
        ExcludeScope.Timesheet => ", not on the Timesheet",
        ExcludeScope.Timeline => ", not in the Timeline",
        ExcludeScope.All => ", excluded everywhere",
        _ => string.Empty,
    };

    // Existing ids, so a new rule's generated id can't collide with one already in the file.
    private static IReadOnlyList<string> ExistingRuleIds()
        => LoadRulesSafe().Select(r => r.Id).ToList();

    // The current rules, in file order — the Rules tab renders these, and edits verify against
    // them. A file that fails to load reads as empty rather than failing the refresh.
    private static IReadOnlyList<ClassificationRule> LoadRulesSafe()
    {
        try
        {
            return File.Exists(TallyPaths.RulesPath) ? RulesFile.Load(TallyPaths.RulesPath) : [];
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load rules.json", ex);
            return [];
        }
    }

    /// <summary>
    /// The rule an edit or delete points at, re-read from the file: the message's index says which
    /// rule, its id proves the table the click happened in wasn't stale. Rules shift position when
    /// one is saved from the Unclassified tab, so acting on index alone could hit the wrong rule.
    /// </summary>
    private (int Index, ClassificationRule Rule)? TargetRule(EditMessage msg)
    {
        if (!int.TryParse(msg.Id, out var index))
            return null;

        var rules = LoadRulesSafe();
        var id = Decode(msg.Key);
        if (id is null || index < 0 || index >= rules.Count || rules[index].Id != id)
        {
            Note("the rules just changed — try that again");
            _ = RefreshAsync();
            return null;
        }

        return (index, rules[index]);
    }

    // An edit committed from the Rules tab. Validation happens here, where the file is written:
    // a rule must keep a category and at least one pattern, and both patterns must compile —
    // a typo'd regex would otherwise silently classify nothing.
    private void UpdateRule(EditMessage msg)
    {
        try
        {
            if (TargetRule(msg) is not { } target)
                return;

            var (index, existing) = target;
            var category = msg.Category?.Trim();
            var process = NullIfEmpty(msg.Process);
            var match = NullIfEmpty(msg.Match);
            if (string.IsNullOrEmpty(category))
            {
                Note("a rule needs a category — nothing saved");
                return;
            }

            if (process is null && match is null)
            {
                Note("a rule needs an app or a window or page pattern — nothing saved");
                return;
            }

            if (BadPattern(process) is not null || BadPattern(match) is not null)
            {
                Note("that pattern isn't a valid regex — nothing saved");
                return;
            }

            // Client is deliberately absent: the Rules tab stopped offering it, so an edit must
            // carry forward whatever the file already says rather than read a control that is no
            // longer there and clear it. A client still reaches a rule through a (?<client>…)
            // capture, or by being typed into rules.json by hand.
            var rule = existing with
            {
                Category = category,
                ProcessPattern = process,
                MatchPattern = match,
                ExcludeFrom = ScopeOf(msg),
            };
            RulesFile.ReplaceRuleAt(TallyPaths.RulesPath, index, rule);
            Log.Info($"Updated classification rule '{rule.Id}' ({category}{ExcludeSuffix(rule.ExcludeFrom)}) from the live view");
            Note($"rule updated — {category}{ExcludeSuffix(rule.ExcludeFrom)}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update a classification rule from the live view", ex);
            Note("couldn't update that rule — see the log");
        }
    }

    // Deleting a rule is cheap to undo in spirit (re-teach it from Unclassified) but still asks
    // first, naming what the rule matched — mirroring how recorded-timer deletion behaves.
    private void DeleteRule(EditMessage msg)
    {
        try
        {
            if (TargetRule(msg) is not { } found)
                return;

            var rule = found.Rule;
            var matches = string.Join("   ", new[]
            {
                rule.ProcessPattern is { } p ? $"app: {p}" : null,
                rule.MatchPattern is { } m ? $"window or page: {m}" : null,
            }.Where(s => s is not null));

            var answer = MessageBox.Show(
                this,
                $"Delete this rule?\n\n{rule.Category}\n{matches}\n\nIts activities go back to Uncategorized, today and in any report generated from now on.",
                "Delete rule",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            // Re-resolve after the dialog: a rule saved from the Unclassified tab meanwhile would
            // have shifted every index.
            if (TargetRule(msg) is not { } current)
                return;

            RulesFile.RemoveRuleAt(TallyPaths.RulesPath, current.Index);
            Log.Info($"Deleted classification rule '{rule.Id}' ({rule.Category}) from the live view");
            Note("rule deleted");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to delete a classification rule from the live view", ex);
            Note("couldn't delete that rule — see the log");
        }
    }

    // "Add rule" from the Rules tab: a hand-written rule, validated like an edit (category, at
    // least one pattern, patterns compile), placed by the same specificity logic Save-rule uses —
    // a window pattern to the top, a page rule after those, an app-only rule to the bottom.
    private void AddRule(EditMessage msg)
    {
        try
        {
            var category = msg.Category?.Trim();
            var process = NullIfEmpty(msg.Process);
            var match = NullIfEmpty(msg.Match);
            if (string.IsNullOrEmpty(category))
            {
                Note("a rule needs a category — nothing added");
                return;
            }

            if (process is null && match is null)
            {
                Note("a rule needs an app or a window or page pattern — nothing added");
                return;
            }

            if (BadPattern(process) is not null || BadPattern(match) is not null)
            {
                Note("that pattern isn't a valid regex — nothing added");
                return;
            }

            var rule = new ClassificationRule
            {
                Id = RuleDraft.ManualId(category, ExistingRuleIds()),
                ProcessPattern = process,
                MatchPattern = match,
                Category = category,
                ExcludeFrom = ScopeOf(msg),
            };
            RulesFile.AddRule(TallyPaths.RulesPath, rule);
            Log.Info($"Added classification rule '{rule.Id}' ({category}{ExcludeSuffix(rule.ExcludeFrom)}) from the Rules tab");
            Note($"rule added — {category}{ExcludeSuffix(rule.ExcludeFrom)}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to add a classification rule from the Rules tab", ex);
            Note("couldn't add that rule — see the log");
        }
    }

    // The Settings tab's Save: the whole form in one message. The page pre-validates, but the
    // file is only written from here, so everything is re-checked where it matters.
    private void SaveSettings(EditMessage msg)
    {
        try
        {
            var start = msg.Start?.Trim();
            var stop = msg.Stop?.Trim();
            if (string.IsNullOrEmpty(start) || !HotkeySpec.TryParse(start, out _, out _)
                || string.IsNullOrEmpty(stop) || !HotkeySpec.TryParse(stop, out _, out _))
            {
                Note("each hotkey needs a modifier plus a key — nothing saved");
                return;
            }

            if (string.Equals(start, stop, StringComparison.OrdinalIgnoreCase))
            {
                Note("start and stop hotkeys must be different — nothing saved");
                return;
            }

            var times = (msg.Times ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => TimeOnly.TryParseExact(t, "HH:mm", out _))
                .Distinct()
                .Order()
                .ToList();
            var retention = int.TryParse(msg.Retention, out var days)
                ? Math.Max(0, days)
                : TallySettings.DefaultEventRetentionDays;

            SettingsWriter.UpdateSettings(TallyPaths.SettingsPath, start, stop, times, retention);
            _hotkeys?.Rebind(start, stop);
            _onSettingsSaved?.Invoke();
            Log.Info($"Settings saved from the live view: start='{start}' stop='{stop}', times=[{string.Join(", ", times)}], retention={retention}d");
            Note("settings saved");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings from the live view", ex);
            Note("couldn't save settings — see the log");
        }
    }

    // "Add category" from the Categories tab: a name plus a colour, into categories.json. Adding a
    // name that already exists just recolours it — the tab's add bar doubling as a colour fix is
    // friendlier than an error.
    private void AddCategory(EditMessage msg)
    {
        try
        {
            var name = msg.Category?.Trim();
            if (string.IsNullOrEmpty(name) || NormalizeHex(msg.Value) is not { } hex)
                return;

            CategoriesFile.Upsert(TallyPaths.CategoriesPath, name, hex);
            Log.Info($"Added category '{name}' ({hex}) from the live view");
            Note($"category added — {name}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to add a category from the live view", ex);
            Note("couldn't add that category — see the log");
        }
    }

    // A swatch changed on the Categories tab. Recolouring ANY name — including a shipped one —
    // stores it as the user's own; the palette prefers those, so it takes effect everywhere.
    private void SetCategoryColor(EditMessage msg)
    {
        try
        {
            var name = Decode(msg.Key)?.Trim();
            if (string.IsNullOrEmpty(name) || NormalizeHex(msg.Value) is not { } hex)
                return;

            CategoriesFile.Upsert(TallyPaths.CategoriesPath, name, hex);
            Log.Info($"Recoloured category '{name}' to {hex} from the live view");
            Note($"colour saved — {name}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to recolour a category from the live view", ex);
            Note("couldn't save that colour — see the log");
        }
    }

    // Renames a category everywhere it lives: its entry in categories.json (keeping the colour)
    // and every rule that files under it. Reversible by renaming back, so no confirmation dialog —
    // the note reports how many rules moved.
    private void RenameCategory(EditMessage msg)
    {
        try
        {
            var oldName = Decode(msg.Key)?.Trim();
            var newName = msg.Category?.Trim();
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)
                || string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            var renamedRules = File.Exists(TallyPaths.RulesPath)
                ? RulesFile.RenameCategory(TallyPaths.RulesPath, oldName, newName)
                : 0;
            CategoriesFile.Rename(TallyPaths.CategoriesPath, oldName, newName);

            Log.Info($"Renamed category '{oldName}' to '{newName}' ({renamedRules} rule(s)) from the live view");
            Note(renamedRules > 0
                ? $"renamed — {renamedRules} rule{(renamedRules == 1 ? "" : "s")} refiled under {newName}"
                : $"category renamed — {newName}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to rename a category from the live view", ex);
            Note("couldn't rename that category — see the log");
        }
    }

    // Deleting removes only the user's entry (name suggestion + colour). Rules using the name keep
    // it — the confirmation says so, so "delete" can't read as bigger than it is.
    private void DeleteCategory(EditMessage msg)
    {
        try
        {
            var name = Decode(msg.Key)?.Trim();
            if (string.IsNullOrEmpty(name))
                return;

            var rulesUsing = LoadRulesSafe().Count(r => string.Equals(r.Category, name, StringComparison.OrdinalIgnoreCase));
            var consequence = rulesUsing > 0
                ? $"The {rulesUsing} rule{(rulesUsing == 1 ? "" : "s")} using it keep the name — only your colour and the suggestion go."
                : "It leaves the suggestion list; its colour returns to standard.";
            var answer = MessageBox.Show(
                this,
                $"Delete the category “{name}”?\n\n{consequence}",
                "Delete category",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            if (CategoriesFile.Remove(TallyPaths.CategoriesPath, name))
            {
                Log.Info($"Deleted category '{name}' from the live view");
                Note("category deleted");
            }

            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to delete a category from the live view", ex);
            Note("couldn't delete that category — see the log");
        }
    }

    /// <summary>A colour the picker produced, normalized ("#rrggbb", lowercase) — or null.</summary>
    private static string? NormalizeHex(string? value)
        => CategoryPalette.HexToRgb(value) is not null
            ? "#" + value!.Trim().TrimStart('#').ToLowerInvariant()
            : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The reason a pattern fails to compile, or null when it's valid (or absent).</summary>
    private static string? BadPattern(string? pattern)
    {
        if (pattern is null)
            return null;

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static string? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records a timer over a past stretch of the day being shown — a claimed idle/locked period
    /// from the Lost time tab, or a hand-entered one from the Timers tab for time Tally never saw.
    /// Recorded timers must never overlap each other (or the one currently running): a timer claims
    /// its whole span on the timesheet, so overlap would bill the same minute twice.
    /// </summary>
    private async Task AddPastTimerAsync(EditMessage msg)
    {
        try
        {
            var name = msg.Value?.Trim();
            if (string.IsNullOrEmpty(name))
                return;

            if (!TimeOnly.TryParseExact(msg.Start?.Trim(), "HH:mm", out var from)
                || !TimeOnly.TryParseExact(msg.Stop?.Trim(), "HH:mm", out var to)
                || to <= from)
            {
                Note("the end time must be after the start — nothing added");
                return;
            }

            // The claim lands on the day on screen, not on today — coming back to yesterday to
            // account for an hour it missed is the reason the day picker exists.
            var day = ViewDate;
            var start = new DateTimeOffset(day.ToDateTime(from));
            var end = new DateTimeOffset(day.ToDateTime(to));
            if (start >= DateTimeOffset.Now)
            {
                Note("that timer hasn't happened yet — nothing added");
                return;
            }

            // A claim that ends before the running timer began cannot overlap it — which is every
            // claim on an earlier day, so this check costs a past day nothing.
            if (_timer.Active is { } running && end > running.StartedAt)
            {
                Note("that overlaps the timer that's running right now — nothing added");
                return;
            }

            await using (var db = new TallyDbContext(_dbOptions))
            {
                var overlapping = await db.ManualTimers.AsNoTracking()
                    .Where(t => t.Start < end && t.End > start)
                    .FirstOrDefaultAsync();
                if (overlapping is not null)
                {
                    Note($"that overlaps the recorded timer “{Shorten(overlapping.Name)}” — nothing added");
                    return;
                }

                db.ManualTimers.Add(new ManualTimer { Name = name, Start = start, End = end });
                await db.SaveChangesAsync();
            }

            Log.Info($"Recorded past timer '{name}' {day:yyyy-MM-dd} {from:HH\\:mm}–{to:HH\\:mm} from the live view");
            Note($"claimed — {name}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to record a past timer from the live view", ex);
            Note("couldn't record that timer — see the log");
        }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..39] + "…";

    // Renames a recorded timer. A blank name is ignored (keeps the existing one).
    private async Task RenameTimerAsync(long id, string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        try
        {
            await using (var db = new TallyDbContext(_dbOptions))
            {
                var timer = await db.ManualTimers.FirstOrDefaultAsync(t => t.Id == id);
                if (timer is null || timer.Name == trimmed)
                    return;
                timer.Name = trimmed;
                await db.SaveChangesAsync();
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to rename timer {id}", ex);
        }
    }

    /// <summary>
    /// Removes a recorded timer. Recorded time is the one thing here that can't be recomputed from
    /// events, so the row is read back and named in a confirmation before anything is deleted — and
    /// the prompt defaults to No.
    /// </summary>
    private async Task DeleteTimerAsync(long id)
    {
        try
        {
            ManualTimer? timer;
            await using (var db = new TallyDbContext(_dbOptions))
                timer = await db.ManualTimers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

            if (timer is null)
                return;

            var answer = MessageBox.Show(
                this,
                $"Delete this recorded timer?\n\n{timer.Name}\n{ReportFormat.Clock(timer.Start)}–{ReportFormat.Clock(timer.End)}  ({ReportFormat.Duration(timer.Duration)})\n\nThis can't be undone.",
                "Delete timer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            await using (var db = new TallyDbContext(_dbOptions))
                await db.ManualTimers.Where(t => t.Id == id).ExecuteDeleteAsync();

            Log.Info($"Deleted recorded timer {id} ('{timer.Name}') from the live view");
            Note("timer deleted");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete timer {id}", ex);
            Note("couldn't delete that timer — see the log");
        }
    }

    private static readonly JsonSerializerOptions EditMessageOptions = new() { PropertyNameCaseInsensitive = true };

    // Every edit the live page can post: a ticket cell (Key/Value), a timer rename (Id/Value), a
    // rule saved from triage (Process/Title/Url as base64 — the activity being filed, which the
    // draft turns into a pattern — plus Scope, Category, Exclude), a Rules-tab add/edit/delete
    // (Id = the rule's index, Key = its id as base64, Match = the pattern as typed; typed values
    // travel plain — they ride the JSON message, not an HTML attribute), or the Settings tab's
    // whole form (Start/Stop hotkeys, Times comma-joined, Retention in days).
    private sealed record EditMessage(
        string? Type, string? Key, string? Id, string? Value,
        string? Process, string? Title, string? Scope, string? Category,
        string? Start, string? Stop, string? Times, string? Retention, string? ExcludeFrom = null,
        string? Url = null, string? Match = null);

    /// <summary>Shows a short-lived note beside the live status, so a saved edit is visibly
    /// acknowledged instead of being erased by the next refresh a moment later.</summary>
    private void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(NoteDuration);
        _statusLabel.Text = $"Live · {text}";
    }

    /// <summary>
    /// Writes the day's Suggestion Export for upload to the att timesheet. The Timesheet tab is
    /// brought up first: the file is only worth writing once it's been looked at, and that tab shows
    /// exactly what it will contain. Nothing is written until a location is chosen.
    /// </summary>
    // async void: WinForms event handler; all awaited work is wrapped in try/catch.
    private async void ExportTimesheet()
    {
        try
        {
            if (_ready)
                await _webView.CoreWebView2.ExecuteScriptAsync("window.tallyShowTab && window.tallyShowTab('timesheet')");

            var date = ViewDate;
            var data = await ReportGenerator.ComputeAsync(_dbOptions, date);
            var entries = JsonExportWriter.BuildEntries(data.Blocks, data.Calls, data.Timers);
            if (entries.Count == 0)
            {
                Note("nothing to export yet");
                return;
            }

            // The export window: pick the slice AND review/edit every entry the file will carry.
            // Cancelling — or narrowing it down to nothing — leaves no file behind.
            if (ExportRangeDialog.Ask(this, entries) is not { } selection)
                return;

            // The window rides in the filename so two slices of one day don't overwrite each other
            // and it's obvious afterwards which is which.
            var slice = (selection.From, selection.To) switch
            {
                (null, null) => string.Empty,
                ({ } f, null) => $"-from{f:HHmm}",
                (null, { } t) => $"-to{t:HHmm}",
                ({ } f, { } t) => $"-{f:HHmm}-{t:HHmm}",
            };

            using var dialog = new SaveFileDialog
            {
                Title = $"Export {selection.Entries.Count} timesheet {(selection.Entries.Count == 1 ? "entry" : "entries")} ({selection.Entries.Sum(e => e.Hours):0.00} h)",
                FileName = $"tally-{date:yyyy-MM-dd}{slice}.json",
                Filter = "Suggestion Export (*.json)|*.json",
                InitialDirectory = _reportsDirectory,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var json = JsonExportWriter.BuildJson(
                date, selection.Entries, data.Calls, data.Timers,
                new JsonExportContext("tally", Environment.MachineName, DateTimeOffset.Now));
            await File.WriteAllTextAsync(dialog.FileName, json);
            Log.Info($"Exported {selection.Entries.Count} timesheet entries to {dialog.FileName}");
            Note($"exported {selection.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Error("Timesheet export failed from the live view", ex);
            Note("export failed — see the log");
        }
    }

    private async void GenerateSnapshot()
    {
        try
        {
            var path = await ReportGenerator.GenerateAsync(_dbOptions, ViewDate, _reportsDirectory, _settings.ResolveReportFormat());
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

    /// <summary>
    /// Shows the window (creating nothing) and resumes live refresh. Re-opening always lands on
    /// today: the window is the current day's dashboard first, and a day picked last week is not
    /// what you meant by clicking the tray icon this morning.
    /// </summary>
    public void ShowLive()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (_ready)
            GoTo(null);
    }

    /// <summary>
    /// Shows the window switched to the named tab — how the tray's Settings entry lands on the
    /// Settings tab. Before the first content has rendered the switch is parked and applied by
    /// the refresh that follows.
    /// </summary>
    public void ShowTab(string name)
    {
        ShowLive();
        if (_ready)
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.tallyShowTab && window.tallyShowTab({JsonSerializer.Serialize(name)})");
        else
            _pendingTab = name;
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
