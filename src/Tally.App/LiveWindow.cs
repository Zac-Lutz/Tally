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
    private readonly Label _titleLabel = new() { Text = "Tally", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold), Margin = new Padding(0, 0, 4, 0) };
    private readonly Label _versionLabel = new() { Text = "", AutoSize = true, ForeColor = MutedFg, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 8, 18, 0) };
    private readonly Label _dateLabel = new() { Text = "", AutoSize = true, ForeColor = MutedFg, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 5, 12, 0) };
    private readonly Label _statusLabel = new() { Text = "Starting…", AutoSize = true, ForeColor = MutedFg, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _timerElapsed = new() { AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), Margin = new Padding(0, 4, 14, 0) };
    private bool _ready;
    private bool _refreshing;
    private string? _note;
    private DateTime _noteUntil;
    private string? _pendingTab;

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

    // One top bar: "Tally", the date, and the live-updated status on the LEFT; the running timer's
    // elapsed time, then export/snapshot/settings on the RIGHT. Starting and naming a timer lives in
    // the Timers tab; only the elapsed figure stays up here, so a running timer is visible from
    // whichever tab you're on.
    private Panel BuildTopBar()
    {
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
            Padding = new Padding(14, 15, 0, 0),
        };
        left.Controls.Add(_titleLabel);
        left.Controls.Add(_versionLabel);
        left.Controls.Add(_dateLabel);
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
            var data = await ReportGenerator.ComputeAsync(_dbOptions, DateOnly.FromDateTime(DateTime.Now));
            var categories = ReportGenerator.LoadCategoriesSafe();

            // Settings are re-read per refresh so the tab always shows the file's truth (a save
            // from this same tab lands on the very next tick).
            var current = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
            var settingsPanel = new SettingsPanelState(
                current.TimerStartHotkey, current.TimerStopHotkey,
                current.ResolveAutoReportTimes().Select(t => t.ToString("HH:mm")).ToList(),
                current.ResolveEventRetentionDays());

            var inner = HtmlReportWriter.BuildMainInner(data.Date, data.Blocks, data.Calls, data.Inactive,
                timers: data.Timers, ticketOverrides: data.TicketOverrides, timerPanel: TimerPanel(),
                rules: LoadRulesSafe(), categories: categories, palette: new CategoryPalette(categories),
                settings: settingsPanel);
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.tallyUpdate({JsonSerializer.Serialize(inner)})");

            if (_pendingTab is { } tab)
            {
                _pendingTab = null;
                await _webView.CoreWebView2.ExecuteScriptAsync(
                    $"window.tallyShowTab && window.tallyShowTab({JsonSerializer.Serialize(tab)})");
            }
            _dateLabel.Text = $"{data.Date:MM-dd-yyyy} · {data.Date.DayOfWeek}";
            var note = DateTime.Now < _noteUntil ? $" · {_note}" : null;
            _statusLabel.Text = $"Live · updated {DateTime.Now:h:mm:ss tt}{note}";
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
                TicketOverrideStore.Set(DateOnly.FromDateTime(DateTime.Now), rowKey, msg.Value);
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
        var category = msg.Category?.Trim();
        if (string.IsNullOrEmpty(category) || (process.Length == 0 && title.Length == 0))
            return;

        try
        {
            var match = string.Equals(msg.Scope, "window", StringComparison.OrdinalIgnoreCase)
                ? RuleMatch.Window
                : RuleMatch.App;
            var rule = RuleDraft.Create(process, title, match, category, ExistingRuleIds());
            RulesFile.AddRule(TallyPaths.RulesPath, rule);
            Log.Info($"Saved classification rule '{rule.Id}' ({category}) from the live view");
            Note($"rule saved — {category}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save a classification rule from the live view", ex);
            Note("couldn't save that rule — see the log");
        }
    }

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
            var title = NullIfEmpty(msg.Title);
            if (string.IsNullOrEmpty(category))
            {
                Note("a rule needs a category — nothing saved");
                return;
            }

            if (process is null && title is null)
            {
                Note("a rule needs an app or window pattern — nothing saved");
                return;
            }

            if (BadPattern(process) is not null || BadPattern(title) is not null)
            {
                Note("that pattern isn't a valid regex — nothing saved");
                return;
            }

            var rule = existing with
            {
                Category = category,
                ProcessPattern = process,
                TitlePattern = title,
                Client = NullIfEmpty(msg.Client),
            };
            RulesFile.ReplaceRuleAt(TallyPaths.RulesPath, index, rule);
            Log.Info($"Updated classification rule '{rule.Id}' ({category}) from the live view");
            Note($"rule updated — {category}");
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
                rule.TitlePattern is { } t ? $"window: {t}" : null,
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
    // a window pattern to the top, an app-only rule to the bottom.
    private void AddRule(EditMessage msg)
    {
        try
        {
            var category = msg.Category?.Trim();
            var process = NullIfEmpty(msg.Process);
            var title = NullIfEmpty(msg.Title);
            if (string.IsNullOrEmpty(category))
            {
                Note("a rule needs a category — nothing added");
                return;
            }

            if (process is null && title is null)
            {
                Note("a rule needs an app or window pattern — nothing added");
                return;
            }

            if (BadPattern(process) is not null || BadPattern(title) is not null)
            {
                Note("that pattern isn't a valid regex — nothing added");
                return;
            }

            var rule = new ClassificationRule
            {
                Id = RuleDraft.ManualId(category, ExistingRuleIds()),
                ProcessPattern = process,
                TitlePattern = title,
                Category = category,
                Client = NullIfEmpty(msg.Client),
            };
            RulesFile.AddRule(TallyPaths.RulesPath, rule);
            Log.Info($"Added classification rule '{rule.Id}' ({category}) from the Rules tab");
            Note($"rule added — {category}");
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
    // rule saved from triage (Process/Title as base64, Scope, Category), a Rules-tab add/edit/
    // delete (Id = the rule's index, Key = its id as base64; typed values travel plain — they
    // ride the JSON message, not an HTML attribute), or the Settings tab's whole form
    // (Start/Stop hotkeys, Times comma-joined, Retention in days).
    private sealed record EditMessage(
        string? Type, string? Key, string? Id, string? Value,
        string? Process, string? Title, string? Scope, string? Category, string? Client,
        string? Start, string? Stop, string? Times, string? Retention);

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

            var date = DateOnly.FromDateTime(DateTime.Now);
            var data = await ReportGenerator.ComputeAsync(_dbOptions, date);
            var wholeDay = SuggestionSlotBuilder.Build(data.Blocks, data.Calls, data.Timers);
            if (wholeDay.Count == 0)
            {
                Note("nothing to export yet");
                return;
            }

            // Ask which slice of the day this file covers before writing anything. Cancelling —
            // or narrowing it down to nothing — leaves no file behind.
            if (ExportRangeDialog.Ask(this, wholeDay) is not { } options)
                return;

            var slots = SuggestionSlotBuilder.Build(data.Blocks, data.Calls, data.Timers, options);

            // The window rides in the filename so two slices of one day don't overwrite each other
            // and it's obvious afterwards which is which.
            var slice = (options.WindowStart, options.WindowEnd) switch
            {
                (null, null) => string.Empty,
                ({ } f, null) => $"-from{f:HHmm}",
                (null, { } t) => $"-to{t:HHmm}",
                ({ } f, { } t) => $"-{f:HHmm}-{t:HHmm}",
            };

            using var dialog = new SaveFileDialog
            {
                Title = $"Export {slots.Count} timesheet {(slots.Count == 1 ? "entry" : "entries")} ({slots.Sum(s => s.Reported.TotalHours):0.00} h)",
                FileName = $"tally-{date:yyyy-MM-dd}{slice}.json",
                Filter = "Suggestion Export (*.json)|*.json",
                InitialDirectory = _reportsDirectory,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            await File.WriteAllTextAsync(dialog.FileName, ReportGenerator.BuildExportJson(data, options));
            Log.Info($"Exported {slots.Count} timesheet slots to {dialog.FileName}");
            Note($"exported {slots.Count} entries");
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
