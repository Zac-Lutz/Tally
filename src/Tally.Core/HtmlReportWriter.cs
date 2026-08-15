using System.Globalization;
using System.Text;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>Renders a day's sessions as a self-contained, theme-aware HTML page for time entry.</summary>
public static class HtmlReportWriter
{
    /// <summary>
    /// A saved snapshot of the day: a self-contained record that can still hand you its timesheet.
    /// Given <paramref name="embeddedJson"/> it carries an Export timesheet button offering the
    /// same range choice the live view does, filtered and downloaded in the page — so a snapshot
    /// taken at 5:30 can be filed the next morning without the app running. What it exports is the
    /// day as it stood when the snapshot was written, which is the point of a snapshot.
    /// </summary>
    public static string BuildHtml(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan? gapThreshold = null,
        IReadOnlyList<ManualTimer>? timers = null,
        IReadOnlyDictionary<string, string>? ticketOverrides = null,
        string? embeddedJson = null,
        CategoryPalette? palette = null)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Tally — {ReportFormat.DisplayDate(date)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timers ?? [],
            gapThreshold ?? TimeSpan.FromMinutes(5), includeHeader: true, editable: false,
            ticketOverrides: ticketOverrides, timerPanel: null, showExport: embeddedJson is not null,
            palette: palette);

        sb.Append("</main>\n");
        sb.Append("<script>").Append(TabScript).Append("</script>\n");
        sb.Append("<script>").Append(RollupGroupScript).Append("</script>\n");

        if (embeddedJson is not null)
        {
            AppendExportDialog(sb);
            // The JSON is already default-encoder-escaped (< > & as \u00xx), so it can't break out
            // of the script element. Everything the button does is built from this embedded copy.
            sb.Append("<script type=\"application/json\" id=\"tally-export\">").Append(embeddedJson).Append("</script>\n");
            sb.Append("<script>").Append(SnapshotExportScript).Append("</script>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // The saved report's range chooser: the live view's dialog, rebuilt as a native <dialog> so a
    // file on disk needs nothing but a browser.
    private static void AppendExportDialog(StringBuilder sb)
    {
        sb.Append("<dialog id=\"xr\">\n");
        sb.Append("<h3>How much of the day should this export cover?</h3>\n");
        sb.Append("<label class=\"xr-all\"><input type=\"checkbox\" id=\"xr-every\" checked> Everything in this snapshot</label>\n");
        sb.Append("<div class=\"xr-times\"><label for=\"xr-from\">From</label><input type=\"time\" id=\"xr-from\">");
        sb.Append("<label for=\"xr-to\">to</label><input type=\"time\" id=\"xr-to\"></div>\n");
        sb.Append("<p id=\"xr-sum\" class=\"xr-sum\"></p>\n");
        sb.Append("<p class=\"hint\">An entry belongs to the window it started in, so two exports never count the same meeting twice.</p>\n");
        sb.Append("<p class=\"hint xr-warn\" id=\"xr-warn\">Importing replaces that day's suggestions in att — log the entries you want before uploading a later slice.</p>\n");
        sb.Append("<div class=\"xr-actions\"><button class=\"tm-go\" id=\"xr-go\">Export</button>");
        sb.Append("<button class=\"tm-del\" id=\"xr-cancel\">Cancel</button></div>\n");
        sb.Append("</dialog>\n");
    }

    /// <summary>
    /// The content that goes INSIDE &lt;main&gt; — the sections BuildHtml renders, without the page
    /// shell, export button, or the Tally/date header (the live window shows that in its own chrome).
    /// The live view swaps this in each refresh, so live and the file report show identical data.
    /// The Rollup's Ticket cells are editable here (the live view is the working surface); the saved
    /// file report renders them read-only.
    /// </summary>
    public static string BuildMainInner(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan? gapThreshold = null,
        IReadOnlyList<ManualTimer>? timers = null,
        IReadOnlyDictionary<string, string>? ticketOverrides = null,
        TimerPanelState? timerPanel = null,
        IReadOnlyList<ClassificationRule>? rules = null,
        IReadOnlyList<CategoryDefinition>? categories = null,
        CategoryPalette? palette = null,
        SettingsPanelState? settings = null)
    {
        var sb = new StringBuilder();
        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timers ?? [],
            gapThreshold ?? TimeSpan.FromMinutes(5), includeHeader: false, editable: true,
            ticketOverrides: ticketOverrides, timerPanel: timerPanel, rules: rules,
            categories: categories, palette: palette, settings: settings);
        return sb.ToString();
    }

    /// <summary>
    /// The one-time page shell for the live view: same styles as the report, an empty
    /// &lt;main id="tally-live"&gt;, and a <c>tallyUpdate(html)</c> function that swaps in fresh
    /// content while preserving scroll position (so the C# side can refresh without a reload).
    /// </summary>
    public static string BuildLiveShell()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>Tally — Live</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        sb.Append("<main id=\"tally-live\"><p class=\"empty\">Loading…</p></main>\n");
        sb.Append("<script>").Append(TabScript).Append("</script>\n");
        sb.Append("<script>").Append(RollupGroupScript).Append("</script>\n");
        sb.Append("<script>").Append(LiveUpdateScript).Append("</script>\n");
        sb.Append("<script>").Append(TicketEditScript).Append("</script>\n");
        sb.Append("<script>").Append(RuleSaveScript).Append("</script>\n");
        sb.Append("<script>").Append(RulesEditScript).Append("</script>\n");
        sb.Append("<script>").Append(ExcludeModeScript).Append("</script>\n");
        sb.Append("<script>").Append(CategoriesScript).Append("</script>\n");
        sb.Append("<script>").Append(SettingsScript).Append("</script>\n");
        sb.Append("<script>").Append(TimerControlScript).Append("</script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendMainInner(
        StringBuilder sb,
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        IReadOnlyList<ManualTimer> timers,
        TimeSpan threshold,
        bool includeHeader,
        bool editable,
        IReadOnlyDictionary<string, string>? ticketOverrides,
        TimerPanelState? timerPanel = null,
        bool showExport = false,
        IReadOnlyList<ClassificationRule>? rules = null,
        IReadOnlyList<CategoryDefinition>? categories = null,
        CategoryPalette? palette = null,
        SettingsPanelState? settings = null)
    {
        if (includeHeader)
        {
            sb.Append("<div class=\"head\">\n");
            sb.Append($"<h1>Tally <span class=\"date\">{ReportFormat.DisplayDate(date)} · {date.DayOfWeek}</span></h1>\n");
            if (showExport)
                sb.Append($"<button class=\"tm-go\" id=\"export-json\" type=\"button\" data-date=\"{date:yyyy-MM-dd}\">Export timesheet</button>\n");
            sb.Append("</div>\n");
        }

        if (blocks.Count == 0 && calls.Count == 0 && timers.Count == 0)
        {
            sb.Append("<p class=\"empty\">No activity recorded.</p>\n");
            return;
        }

        // Uncategorized and lost time are computed once, up here: their totals are summary cards
        // and their details are tabs, and the two must agree.
        var uncategorized = UnclassifiedBuilder.Build(blocks);
        var (lostStretches, lostTotal) = LostTime(blocks, inactivePeriods, timers, threshold);

        AppendSummary(sb, blocks, calls, inactivePeriods, lostTotal, uncategorized.Count);
        AppendTabs(sb, blocks, calls, timers, editable, ticketOverrides,
            timerPanel, rules, categories, palette, settings, uncategorized, lostStretches, lostTotal);
    }

    // Rollup / Calls / Timeline / Timers / Uncategorized as switchable tabs (Rollup active by
    // default) instead of stacked sections. Tab switching + preserving the choice across live
    // refreshes is TabScript. Uncategorized and lost-time totals live in the summary cards, so
    // the tab strip stays just names.
    private static void AppendTabs(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers,
        bool editable, IReadOnlyDictionary<string, string>? ticketOverrides,
        TimerPanelState? timerPanel, IReadOnlyList<ClassificationRule>? rules,
        IReadOnlyList<CategoryDefinition>? categories, CategoryPalette? palette,
        SettingsPanelState? settings,
        IReadOnlyList<UnclassifiedRow> uncategorized, IReadOnlyList<LostStretch> lostStretches, TimeSpan lostTotal)
    {
        sb.Append("<div class=\"tabs\">");
        sb.Append("<button class=\"tab active\" type=\"button\" data-tab=\"rollup\">Rollup</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timesheet\">Timesheet</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timeline\">Timeline</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"tickets\">Tickets</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"calls\">Calls</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timers\">Timers</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"unclassified\">Uncategorized</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"lost\">Lost time</button>");
        // The Rules and Categories tabs exist only where their data was provided — the live view.
        // A saved report is a record of a day; the app's current configuration doesn't belong in it.
        if (rules is not null)
            sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"rules\">Rules</button>");
        if (categories is not null)
            sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"categories\">Categories</button>");
        if (settings is not null)
            sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"settings\">Settings</button>");
        sb.Append("</div>\n");

        // Always the whole day: choosing a slice belongs to the export itself, so this stays the
        // one honest picture of what happened rather than a filtered one.
        sb.Append("<section class=\"panel\" data-panel=\"timesheet\">\n");
        AppendTimesheet(sb, SuggestionSlotBuilder.Build(blocks, calls, timers), palette);
        sb.Append("</section>\n");

        sb.Append("<section class=\"panel active\" data-panel=\"rollup\">\n");
        AppendRollup(sb, blocks, calls, timers, editable, ticketOverrides, palette);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timeline\">\n");
        AppendTimeline(sb, blocks, palette);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"tickets\">\n");
        AppendTickets(sb, TicketsBuilder.Build(blocks, calls, ticketOverrides), palette);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"calls\">\n");
        AppendCalls(sb, calls, palette);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timers\">\n");
        AppendTimers(sb, timers, editable, timerPanel);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"unclassified\">\n");
        AppendUnclassified(sb, uncategorized, blocks, editable);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"lost\">\n");
        AppendLostTime(sb, lostStretches, lostTotal, editable);
        sb.Append("</section>\n");
        if (rules is not null)
        {
            sb.Append("<section class=\"panel\" data-panel=\"rules\">\n");
            AppendRules(sb, rules, palette);
            sb.Append("</section>\n");
        }

        if (categories is not null)
        {
            sb.Append("<section class=\"panel\" data-panel=\"categories\">\n");
            AppendCategories(sb, categories, rules, palette);
            sb.Append("</section>\n");
        }

        if (settings is not null)
        {
            sb.Append("<section class=\"panel\" data-panel=\"settings\">\n");
            AppendSettings(sb, settings);
            sb.Append("</section>\n");
        }

        // Category suggestions for the triage and rules inputs: categories seen today, the user's
        // own, and the shipped defaults, so the day's naming stays consistent (free text still
        // wins — a datalist only proposes).
        if (editable)
        {
            sb.Append("<datalist id=\"uc-cats\">");
            foreach (var category in KnownCategories(blocks, categories))
                sb.Append($"<option value=\"{Esc(category)}\"></option>");
            sb.Append("</datalist>\n");
        }
    }

    // The rules manager: every rule in the order they're tried, each row editable in place or
    // deletable. The page only toggles a row between its read view and its inputs; the C# host
    // does the writing, and the refresh that follows re-renders the table from the file — so what
    // the table shows is always what rules.json actually says.
    private static void AppendRules(StringBuilder sb, IReadOnlyList<ClassificationRule> rules, CategoryPalette? palette)
    {
        sb.Append("<p class=\"hint\">Every rule Tally classifies with, tried top to bottom — the <strong>first match wins</strong>. Patterns are case-insensitive regexes; an edit re-sorts today within seconds and applies to every report generated from now on. Deleting a rule sends its activities back to Uncategorized.</p>\n");

        // Hand-writing a rule, without leaving the tab. Placement is decided by the same
        // specificity logic Save-rule uses: a window pattern earns the top, app-only the bottom.
        sb.Append("<div class=\"rl-addbar\">")
          .Append("<input class=\"rl-new-cat\" type=\"text\" list=\"uc-cats\" placeholder=\"Category\" aria-label=\"New rule category\">")
          .Append("<input class=\"rl-new-proc\" type=\"text\" placeholder=\"App pattern (regex)\" aria-label=\"New rule app pattern\">")
          .Append("<input class=\"rl-new-title\" type=\"text\" placeholder=\"Window pattern (regex)\" aria-label=\"New rule window pattern\">")
          .Append("<input class=\"rl-new-url\" type=\"text\" placeholder=\"Page pattern (regex)\" aria-label=\"New rule page pattern\">")
          .Append("<input class=\"rl-new-client\" type=\"text\" placeholder=\"Client (optional)\" aria-label=\"New rule client\">")
          .Append(ExcludeControls("rl-new-exclude", ExcludeScope.None, "New rule"))
          .Append("<button class=\"uc-save rl-add-btn\" type=\"button\">Add rule</button>")
          .Append("</div>\n");

        if (rules.Count == 0)
        {
            sb.Append("<p class=\"empty\">No rules yet — add one above, or save one from the Uncategorized tab.</p>\n");
            return;
        }

        sb.Append("<div class=\"scroll\">\n<table class=\"rules\">\n<thead>\n<tr><th class=\"num\">#</th><th>Category</th><th>App matches</th><th>Window matches</th><th>Page matches</th><th>Client</th><th>Exclude</th><th></th></tr>\n</thead>\n<tbody>\n");

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            sb.Append($"<tr class=\"rl\" data-i=\"{i}\" data-id=\"{B64(rule.Id)}\">")
              .Append($"<td class=\"num muted\">{i + 1}</td>")
              .Append("<td>")
              .Append($"<span class=\"rl-view\">{CategoryBadge(rule.Category, palette)}</span>")
              .Append($"<input class=\"rl-in rl-cat\" type=\"text\" list=\"uc-cats\" value=\"{Esc(rule.Category)}\" aria-label=\"Category\">")
              .Append("</td>");
            AppendRuleCell(sb, rule.ProcessPattern, "rl-proc", "any app", "App pattern (regex)");
            AppendRuleCell(sb, rule.TitlePattern, "rl-title", "any window", "Window pattern (regex)");
            AppendRuleCell(sb, rule.UrlPattern, "rl-url", "any page", "Page pattern (regex)");
            AppendRuleCell(sb, rule.Client, "rl-client", "—", "Client (optional)");
            sb.Append("<td>")
              .Append(rule.ExcludeFrom is ExcludeScope.None
                  ? "<span class=\"rl-view muted\">—</span>"
                  : $"<span class=\"rl-view rl-ex-yes\">{Esc(ExcludeScopeLabel(rule.ExcludeFrom))}</span>")
              .Append("<span class=\"rl-in ex-pair\">")
              .Append(ExcludeControls("rl-exclude", rule.ExcludeFrom, "Counted or excluded"))
              .Append("</span>")
              .Append("</td>");
            sb.Append("<td class=\"num rl-actions\">")
              .Append("<span class=\"rl-view\"><button class=\"uc-save rl-edit\" type=\"button\">Edit</button> <button class=\"tm-del rl-del\" type=\"button\">Delete</button></span>")
              .Append("<span class=\"rl-in\"><button class=\"uc-save rl-ok\" type=\"button\">Save</button> <button class=\"tm-del rl-cancel\" type=\"button\">Cancel</button></span>")
              .Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        sb.Append("<p class=\"hint\">A blank app, window or page pattern means “any”; a rule needs at least one of the three, and every one it has must match. <strong>Page</strong> matches the website address as Tally stores it — host and path, no <code>?</code> query — so <code>^halo\\.lutz\\.us</code> is any Halo page. The named groups <code>(?&lt;ticket&gt;…)</code>, <code>(?&lt;client&gt;…)</code>, and <code>(?&lt;subject&gt;…)</code> work in a window <em>or</em> page pattern; the window's win when a rule has both. <strong>Exclude</strong> chooses which account of the day leaves the activity out: <em>Rollup</em> tidies that tab while the time still bills, <em>Timesheet</em> keeps it off the timesheet, the export, and the Tickets tab, and <em>All</em> does both. The Timeline always shows it, so the day's record stays honest.</p>\n");
    }

    // The Settings tab — the WinForms dialog's contents, moved into the page so configuration
    // lives beside the tabs it affects. The form is stateful (chips added, hotkeys captured)
    // until Save posts the whole thing to the host in one message; the "st-dirty" class the
    // script maintains keeps live refreshes from wiping half-made changes.
    private static void AppendSettings(StringBuilder sb, SettingsPanelState settings)
    {
        sb.Append("<div class=\"st-form\">\n");

        sb.Append("<h2>Timer hotkeys</h2>\n");
        sb.Append($"<div class=\"st-row\"><label>Start timer</label><input class=\"st-hk\" type=\"text\" readonly value=\"{Esc(settings.StartHotkey)}\" aria-label=\"Start timer hotkey\"></div>\n");
        sb.Append($"<div class=\"st-row\"><label>Stop timer</label><input class=\"st-hk\" type=\"text\" readonly value=\"{Esc(settings.StopHotkey)}\" aria-label=\"Stop timer hotkey\"></div>\n");
        sb.Append("<p class=\"hint\">Click a field, then press the combination you want — Ctrl/Alt/Shift plus a letter, digit, or F-key. They work from anywhere, even when Tally isn't focused.</p>\n");

        sb.Append("<h2>Auto-generate reports at these times</h2>\n<div class=\"st-times\">");
        foreach (var time in settings.AutoReportTimes)
        {
            var display = TimeOnly.TryParseExact(time, "HH:mm", out var parsed)
                ? parsed.ToString("h:mm tt", CultureInfo.InvariantCulture)
                : time;
            sb.Append($"<span class=\"st-time\" data-t=\"{Esc(time)}\">{Esc(display)}<button class=\"st-time-del\" type=\"button\" aria-label=\"Remove {Esc(display)}\">×</button></span>");
        }

        sb.Append("</div>\n");
        sb.Append("<div class=\"st-row\"><input type=\"time\" class=\"st-time-new\" aria-label=\"New report time\"> <button class=\"uc-save st-time-add\" type=\"button\">Add time</button></div>\n");
        sb.Append("<p class=\"hint\">Each time writes that day's report automatically. Remove them all to turn auto-reports off.</p>\n");

        sb.Append("<h2>Keep raw history</h2>\n");
        var forever = settings.RetentionDays <= 0;
        var days = forever ? 90 : settings.RetentionDays;   // placeholder while disabled
        sb.Append($"<div class=\"st-row\"><label>Delete raw activity older than</label><input type=\"number\" class=\"st-ret\" min=\"7\" max=\"3650\" value=\"{days}\"{(forever ? " disabled" : string.Empty)} aria-label=\"Days of raw history to keep\"> <span class=\"muted\">days</span></div>\n");
        sb.Append($"<label class=\"st-forever\"><input type=\"checkbox\" class=\"st-keep-forever\"{(forever ? " checked" : string.Empty)}> Keep everything forever</label>\n");
        sb.Append("<p class=\"hint\">Saved reports and timers are never deleted.</p>\n");

        sb.Append("<div class=\"st-actions\"><button class=\"tm-go st-save\" type=\"button\">Save settings</button><span class=\"st-msg\"></span></div>\n");
        sb.Append("</div>\n");
    }

    // The Categories tab: every category in play — the user's own, the ones rules file under, the
    // shipped suggestions, and the app's built-ins — each with its colour. Changing any swatch
    // stores that colour as the user's own; Rename refiles the rules too (the host does that);
    // Delete removes only the user's entry — rules still using the name keep it, on the standard
    // colour. The host writes categories.json; the refresh re-renders this table from the file.
    private static void AppendCategories(
        StringBuilder sb, IReadOnlyList<CategoryDefinition> categories,
        IReadOnlyList<ClassificationRule>? rules, CategoryPalette? palette)
    {
        sb.Append("<p class=\"hint\">Add your own categories and pick each one's colour — it colours the Rollup, Timeline, and the Timesheet calendar, in the live view and in saved reports. Renaming a category also refiles every rule that uses it.</p>\n");

        sb.Append("<div class=\"ct-addbar\">")
          .Append("<input type=\"color\" class=\"ct-new-color\" value=\"#8b5cf6\" aria-label=\"New category colour\">")
          .Append("<input type=\"text\" class=\"ct-new-name\" placeholder=\"New category name\" aria-label=\"New category name\">")
          .Append("<button class=\"uc-save ct-add-btn\" type=\"button\">Add category</button>")
          .Append("</div>\n");

        var ruleCounts = (rules ?? [])
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var custom = categories.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] builtIn =
        [
            RollupBuilder.CallCategory, RollupBuilder.TimerCategory,
            SuggestionSlotBuilder.OddsAndEndsCategory, CallApps.TeamsCallCategory, TeamsChatCategory,
        ];

        var names = categories.Select(c => c.Name)
            .Concat(ruleCounts.Keys)
            .Concat(BaselineCategories)
            .Concat(builtIn)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        sb.Append("<div class=\"scroll\">\n<table class=\"cats\">\n<thead>\n<tr><th>Colour</th><th>Category</th><th>Used by</th><th></th></tr>\n</thead>\n<tbody>\n");

        foreach (var name in names)
        {
            var hex = CategoryPalette.RgbToHex(CategoryRgb(name, palette));
            var isCustom = custom.Contains(name);
            var count = ruleCounts.GetValueOrDefault(name);
            var usedBy = count > 0 ? $"{count} rule{(count == 1 ? "" : "s")}"
                : builtIn.Contains(name, StringComparer.OrdinalIgnoreCase) ? "built-in"
                : isCustom ? "custom" : "suggestion";

            sb.Append($"<tr class=\"ct\" data-name=\"{B64(name)}\">")
              .Append($"<td><input type=\"color\" class=\"ct-color\" value=\"{hex}\" aria-label=\"Colour for {Esc(name)}\"></td>")
              .Append("<td>")
              .Append($"<span class=\"ct-view\">{CategoryBadge(name, palette)}</span>")
              .Append($"<input class=\"ct-in ct-name\" type=\"text\" value=\"{Esc(name)}\" aria-label=\"Category name\">")
              .Append("</td>")
              .Append($"<td class=\"muted\">{Esc(usedBy)}</td>")
              .Append("<td class=\"num ct-actions\"><span class=\"ct-view\">");
            if (count > 0 || isCustom)
                sb.Append("<button class=\"uc-save ct-rename\" type=\"button\">Rename</button> ");
            if (isCustom)
                sb.Append("<button class=\"tm-del ct-del\" type=\"button\">Delete</button>");
            sb.Append("</span><span class=\"ct-in\"><button class=\"uc-save ct-ok\" type=\"button\">Save</button> <button class=\"tm-del ct-cancel\" type=\"button\">Cancel</button></span>")
              .Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        sb.Append("<p class=\"hint\">“Suggestion” names are offered in category pickers; “built-in” ones are the app's own (Call, Timer…). Any of them can be recoloured — that stores it as yours.</p>\n");
    }

    // One value cell of a rules row: the read view (pattern as code, or a muted placeholder when
    // absent) and the hidden input the row's edit mode reveals.
    private static void AppendRuleCell(StringBuilder sb, string? value, string cssClass, string emptyText, string ariaLabel)
    {
        sb.Append("<td>")
          .Append(value is null
              ? $"<span class=\"rl-view muted\">{Esc(emptyText)}</span>"
              : $"<span class=\"rl-view\"><code>{Esc(value)}</code></span>")
          .Append($"<input class=\"rl-in {cssClass}\" type=\"text\" value=\"{Esc(value ?? string.Empty)}\" aria-label=\"{Esc(ariaLabel)}\">")
          .Append("</td>");
    }

    /// <summary>
    /// The timesheet preview: exactly the entries the JSON export will contain, so what uploads can
    /// be checked before it's uploaded. Measured time is shown beside the reported figure — the
    /// rounding is visible rather than something the file does quietly.
    /// </summary>
    private static void AppendTimesheet(StringBuilder sb, IReadOnlyList<SuggestionSlot> slots, CategoryPalette? palette)
    {
        if (slots.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing to put on a timesheet yet.</p>\n");
            return;
        }

        var total = slots.Sum(s => s.Reported.TotalHours);
        var measured = TimeSpan.FromTicks(slots.Sum(s => s.Measured.Ticks));
        sb.Append($"<p class=\"hint\">{slots.Count} {(slots.Count == 1 ? "entry" : "entries")} · <strong>{total:0.00} h</strong> to enter · {ReportFormat.Duration(measured)} actually measured. This is exactly what the export contains.</p>\n");

        AppendCalendar(sb, slots, palette);
        sb.Append("<p class=\"hint\">Blocks sit where the work happened; the number on each is the hours to enter. Work you kept coming back to — a ticket revisited between other windows — draws as one faint stretch from its first visit to its last, with solid pins marking the visits themselves (hover for their exact times); the hours are still only the time measured. Time is claimed once — a timer beats a meeting, a meeting beats whatever window was open during it. Anything too short to stand alone is gathered into the “odds and ends” block rather than dropped.</p>\n");
    }

    /// <summary>How many pixels one minute of the day is drawn as.</summary>
    private const double MinuteHeight = 1.35;

    /// <summary>Floor height for a block, so a five-minute entry still shows its label.</summary>
    private const double MinEventHeight = 19;

    // The day as a calendar grid: an hour ruler down the left and each slot placed against it.
    // Blocks are drawn over their real span (the shape att's own calendar will show them in), with
    // the billable hours on the block — the two differ whenever short gaps were bridged, and the
    // gaps between blocks are the point: unaccounted time is visible as empty space.
    private static void AppendCalendar(StringBuilder sb, IReadOnlyList<SuggestionSlot> slots, CategoryPalette? palette)
    {
        if (TimesheetCalendar.Bounds(slots) is not { } bounds)
            return;

        var totalMinutes = (bounds.End - bounds.Start).TotalMinutes;
        sb.Append($"<div class=\"cal\" style=\"height:{Px(totalMinutes * MinuteHeight)}px\">\n");

        for (var line = bounds.Start; line < bounds.End; line += TimesheetCalendar.GridStep)
        {
            var top = (line - bounds.Start).TotalMinutes * MinuteHeight;
            // Labelled on the hour; the half hours rule faintly so a block's length is readable
            // without crowding the gutter with numbers.
            sb.Append(line.Minute == 0
                ? $"<div class=\"cal-hr\" style=\"top:{Px(top)}px\"><span>{ReportFormat.Clock(line)}</span></div>\n"
                : $"<div class=\"cal-hr half\" style=\"top:{Px(top)}px\"></div>\n");
        }

        sb.Append("<div class=\"cal-ev\">\n");
        foreach (var entry in TimesheetCalendar.Lay(slots))
        {
            var slot = entry.Slot;
            var (displayStart, displayEnd) = TimesheetCalendar.DisplaySpan(slot);
            var span = displayEnd - displayStart;
            var top = (displayStart - bounds.Start).TotalMinutes * MinuteHeight;
            var height = Math.Max(MinEventHeight, span.TotalMinutes * MinuteHeight);
            var width = 100d / entry.Columns;
            var left = entry.Column * width;

            var rgb = CategoryRgb(slot.Category, palette);
            var ticket = slot.TicketRef is { } t ? $"#{t} " : string.Empty;
            var visits = slot.Kind == SuggestionSlotKind.Activity ? TimesheetCalendar.Visits(slot) : [];
            var tip = $"{ReportFormat.Clock(displayStart)}–{ReportFormat.Clock(displayEnd)} · {ticket}{slot.Label} · "
                      + $"{ReportFormat.Duration(slot.Measured)} measured → {slot.Reported.TotalHours:0.00} h to enter"
                      + VisitsTip(visits);

            sb.Append($"<div class=\"ev\" style=\"top:{Px(top)}px;height:{Px(height - 2)}px;")
              .Append($"left:calc({Px(left)}% + 1px);width:calc({Px(width)}% - 3px);")
              .Append($"background:rgba({rgb},.10);border-left-color:rgba({rgb},.9)\" title=\"{Esc(tip)}\">");

            if (visits.Count > 1)
            {
                // Work revisited across the envelope: each sitting is a pin at the time it really
                // happened, so the block reads "returned to three times over the hour" rather than
                // one merged smear.
                foreach (var (visitStart, visitEnd) in visits)
                {
                    var pinTop = Math.Clamp((visitStart - displayStart).TotalMinutes / span.TotalMinutes * 100, 0, 100);
                    var pinHeight = Math.Clamp((visitEnd - visitStart).TotalMinutes / span.TotalMinutes * 100, 0, 100 - pinTop);
                    sb.Append($"<i class=\"ev-pin\" style=\"top:{Px(pinTop)}%;height:{Px(pinHeight)}%;background:rgba({rgb},.32)\"></i>");
                }
            }
            else
            {
                // One continuous stretch (or a call/timer/odds-and-ends): the solid part is the
                // time actually measured, so a block that's mostly empty reads as mostly empty.
                var fill = span > TimeSpan.Zero
                    ? Math.Clamp(slot.Measured.TotalMinutes / span.TotalMinutes * 100, 0, 100)
                    : 100;
                sb.Append($"<i class=\"ev-fill\" style=\"height:{Px(fill)}%;background:rgba({rgb},.22)\"></i>");
            }

            sb.Append("<span class=\"ev-txt\">")
              .Append($"<b>{slot.Reported.TotalHours:0.00}</b> ")
              .Append(Esc(ticket + slot.Label))
              .Append("</span></div>\n");
        }

        sb.Append("</div>\n</div>\n");
    }

    private static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // The hover's visit list — the exact times behind each pin. Capped so a day of constant
    // switching doesn't produce a tooltip taller than the screen.
    private static string VisitsTip(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> visits)
    {
        if (visits.Count <= 1)
            return string.Empty;

        const int shown = 6;
        var list = string.Join(", ", visits.Take(shown)
            .Select(v => $"{ReportFormat.Clock(v.Start)}–{ReportFormat.Clock(v.End)}"));
        var more = visits.Count > shown ? $" +{visits.Count - shown} more" : string.Empty;
        return $" · {visits.Count} visits: {list}{more}";
    }

    // The triage list: everything that matched no rule, one row per app+window. In the live view each
    // row can be given a category and saved as a rule on the spot (the C# host writes rules.json and
    // the next refresh reclassifies the day). The saved file report lists them read-only.
    private static void AppendUnclassified(
        StringBuilder sb, IReadOnlyList<UnclassifiedRow> rows, IReadOnlyList<ClassifiedBlock> blocks, bool editable)
    {
        if (rows.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing uncategorized — every activity today matched a rule.</p>\n");
            return;
        }

        if (editable)
            sb.Append("<p class=\"hint\">Give an activity a category and save it as a rule. It applies to today straight away, and to every day from here. <strong>Exclude</strong> leaves it out of an account of the day: <em>Rollup</em> tidies that tab only, <em>Timesheet</em> keeps it off the timesheet and the export, <em>All</em> does both. The Timeline always keeps it.</p>\n");

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n<tr><th>App</th><th>Window</th><th class=\"num\">Time</th>");
        if (editable)
            sb.Append("<th>Category</th><th>Applies to</th><th>Exclude</th><th></th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var row in rows)
        {
            var host = RuleDraft.HostOf(row.Url);
            sb.Append($"<tr data-p=\"{B64(row.ProcessName)}\" data-t=\"{B64(row.Title)}\"")
              .Append(row.Url is { } address ? $" data-u=\"{B64(address)}\"" : string.Empty)
              .Append('>')
              .Append("<td>").Append(Esc(row.ProcessName)).Append("</td>")
              .Append("<td>").Append(Esc(row.Title));
            // The page behind a mystery tab — often the clue the title withheld.
            if (row.Url is { } url)
                sb.Append($"<div class=\"uc-url muted\">{Esc(url)}</div>");
            sb.Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(row.Time)).Append("</td>");
            if (editable)
            {
                sb.Append("<td><input class=\"uc-cat\" type=\"text\" list=\"uc-cats\" placeholder=\"Category\" aria-label=\"Category\"></td>")
                  .Append("<td><select class=\"uc-scope\" aria-label=\"Applies to\">")
                  .Append($"<option value=\"app\">Any {Esc(row.ProcessName)} window</option>")
                  .Append("<option value=\"window\">Only this window</option>")
                  // Offered only where there's a page to key on — the site is usually the truer
                  // answer for a browser tab, whose title changes far more often than its address.
                  .Append(host is not null ? $"<option value=\"site\">Any page on {Esc(host)}</option>" : string.Empty)
                  .Append("</select></td>")
                  .Append("<td class=\"ex-pair\">").Append(ExcludeControls("uc-exclude", ExcludeScope.None, "Counted or excluded")).Append("</td>")
                  .Append("<td class=\"num\"><button class=\"uc-save\" type=\"button\">Save rule</button></td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    // Categories seen today, plus the shipped defaults so a fresh day still offers sensible names.
    private static readonly string[] BaselineCategories =
    [
        "Admin", "Development", "Discord", "Halo", "IT Glue", "Meetings",
        "Outlook", "RingCentral", "ScreenConnect", "Teams",
    ];

    private static IReadOnlyList<string> KnownCategories(
        IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CategoryDefinition>? categories)
        => blocks
            .Select(b => b.Classification.Category)
            .Where(c => c != Classification.Unclassified)
            .Concat((categories ?? []).Select(c => c.Name))
            .Concat(BaselineCategories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Process names and titles travel to the host as base64 so no quoting or escaping can mangle
    // them on the way through an HTML attribute and a JSON message.
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    // The Timers tab: what's already recorded on top, and the control to run the next one below it,
    // so a finished timer joins the list right above the field you started it from. In the live view
    // a recorded name is editable (the change persists and reflects on the Rollup); the saved file
    // report shows names read-only and has no control at all.
    private static void AppendTimers(
        StringBuilder sb, IReadOnlyList<ManualTimer> timers, bool editable, TimerPanelState? panel)
    {
        if (timers.Count == 0)
        {
            sb.Append("<p class=\"empty\">No timers recorded today.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
            sb.Append("<tr><th>Timer</th><th>Start</th><th>End</th><th class=\"num\">Duration</th>");
            if (editable)
                sb.Append("<th></th>");
            sb.Append("</tr>\n</thead>\n<tbody>\n");
            foreach (var t in timers.OrderByDescending(t => t.Start))
            {
                var nameCell = editable
                    ? $"<input class=\"tn\" type=\"text\" data-timer-id=\"{t.Id}\" value=\"{Esc(t.Name)}\" aria-label=\"Timer name\">"
                    : Esc(t.Name);
                sb.Append("<tr><td>").Append(nameCell).Append("</td>")
                  .Append("<td>").Append(ReportFormat.Clock(t.Start)).Append("</td>")
                  .Append("<td>").Append(ReportFormat.Clock(t.End)).Append("</td>")
                  .Append("<td class=\"num\">").Append(ReportFormat.Duration(t.Duration)).Append("</td>");
                if (editable)
                    sb.Append($"<td class=\"num\"><button class=\"tm-del\" type=\"button\" data-timer-id=\"{t.Id}\" title=\"Delete this recorded timer\">Delete</button></td>");
                sb.Append("</tr>\n");
            }

            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        if (editable && panel is not null)
            AppendTimerControl(sb, panel);
    }

    private static void AppendTimerControl(StringBuilder sb, TimerPanelState panel)
    {
        sb.Append("<div class=\"tmbar\">");
        sb.Append($"<input class=\"tm-name\" type=\"text\" placeholder=\"Timer name\" value=\"{Esc(panel.Name)}\" aria-label=\"Timer name\">");
        sb.Append(panel.StartedAt is not null
            ? "<button class=\"tm-go stop\" type=\"button\">Stop</button>"
            : "<button class=\"tm-go\" type=\"button\">Start</button>");

        // The elapsed figure is rendered here and re-rendered on every refresh; between refreshes a
        // script ticks it from data-started so the seconds move like a stopwatch should.
        if (panel.StartedAt is { } started)
        {
            var iso = started.ToString("o", CultureInfo.InvariantCulture);
            sb.Append($"<span class=\"tm-elapsed\" data-started=\"{iso}\">{TimerText.Elapsed(panel.Elapsed)}</span>");
        }

        sb.Append("</div>\n");
        sb.Append(panel.StartedAt is not null
            ? "<p class=\"hint\">Renaming while it runs renames the timer; stopping files it above.</p>\n"
            : "<p class=\"hint\">Name it and press Start — or use the hotkeys, which work from anywhere.</p>\n");

        // Backfill: a timer for time Tally never saw at all — the machine off, an onsite visit.
        // Claiming from the Lost time tab covers idle/locked stretches; this covers the rest.
        sb.Append("<div class=\"tm-pastbar\">")
          .Append("<input type=\"time\" class=\"tm-past-from\" aria-label=\"Past timer from\"> ")
          .Append("<input type=\"time\" class=\"tm-past-to\" aria-label=\"Past timer to\"> ")
          .Append("<input class=\"tm-past-name\" type=\"text\" placeholder=\"Add a past timer — e.g. Onsite at Acme\" aria-label=\"Past timer name\"> ")
          .Append("<button class=\"uc-save tm-past-add\" type=\"button\">Add past timer</button>")
          .Append("</div>\n");
        sb.Append("<p class=\"hint\">For time today that Tally never saw — the laptop closed, a site visit. It files above once added, and bills like any timer.</p>\n");
    }

    private static void AppendSummary(
        StringBuilder sb,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactive,
        TimeSpan lostTotal,
        int uncategorizedCount)
    {
        // Excluded time is recorded and real, so it stays in Total; it just isn't work, so it
        // leaves Active. The test is whether it reaches the Timesheet: a Rollup-only exclusion
        // tidies one view and still bills, so counting it as anything but Active would lie.
        var excluded = TimeSpan.FromTicks(
            blocks.Where(b => b.Classification.ExcludedFromTimesheet).Sum(b => b.Block.Duration.Ticks));
        var active = TimeSpan.FromTicks(
            blocks.Where(b => !b.Classification.ExcludedFromTimesheet).Sum(b => b.Block.Duration.Ticks));
        var callTime = TimeSpan.FromTicks(calls.Sum(c => c.Duration.Ticks));
        var inactiveTime = TimeSpan.FromTicks(inactive.Sum(p => p.Duration.Ticks));

        if (blocks.Count > 0 || calls.Count > 0)
        {
            var first = blocks.Count > 0 ? blocks[0].Block.Start : calls[0].Start;
            var last = blocks.Count > 0 ? blocks[^1].Block.End : calls[^1].End;
            sb.Append($"<p class=\"tracked\">Tracked {ReportFormat.Clock(first)}–{ReportFormat.Clock(last)}</p>\n");
        }

        // Total = all recorded wall-clock (active work + idle/locked). Calls and manual timers
        // overlay that time rather than adding to it, so they're not summed in. Lost time is the
        // "how much is unaccounted for" figure; Uncategorized is the count of activities still
        // needing a rule — their tabs hold the detail.
        sb.Append("<div class=\"cards\">\n");
        Card(sb, "Total", ReportFormat.Duration(active + excluded + inactiveTime));
        Card(sb, "Active", ReportFormat.Duration(active));
        Card(sb, "Calls", ReportFormat.Duration(callTime));
        Card(sb, "Inactive", ReportFormat.Duration(inactiveTime));
        // Only worth a card once there's something in it — a permanent 0s would be noise for
        // anyone who never writes an excluding rule.
        if (excluded > TimeSpan.Zero)
            Card(sb, "Excluded", ReportFormat.Duration(excluded));
        Card(sb, "Lost time", ReportFormat.Duration(lostTotal));
        Card(sb, "Uncategorized", uncategorizedCount.ToString());
        sb.Append("</div>\n");
    }

    /// <summary>One stretch on no timesheet line. Idle/locked stretches are claimable — "that was
    /// a phone call" turns them into a recorded timer; uncategorized ones want a rule instead.</summary>
    private sealed record LostStretch(DateTimeOffset Start, DateTimeOffset End, string What, bool Claimable)
    {
        public TimeSpan Duration => End - Start;
    }

    /// <summary>
    /// Stretches of the day that ended up on no timesheet line: idle or locked time, and activity
    /// that matched no rule. Both are "time you'll have to account for from memory", which is why
    /// they share a tab — the Uncategorized tab is for teaching Tally a rule, this one is for
    /// spotting the hole before someone asks about it. Time a recorded timer covers is subtracted
    /// first: a claimed stretch IS on a timesheet line now, so only its unclaimed remainder (if
    /// still over the threshold) stays lost.
    /// </summary>
    private static (List<LostStretch> Stretches, TimeSpan Total) LostTime(
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<InactivePeriod> inactive,
        IReadOnlyList<ManualTimer> timers,
        TimeSpan threshold)
    {
        var claimed = SuggestionSlotBuilder.Merge(
            timers.Where(t => t.End > t.Start).Select(t => new SuggestionSlotBuilder.Span(t.Start, t.End)));

        IEnumerable<LostStretch> Unclaimed(DateTimeOffset start, DateTimeOffset end, string what, bool claimable)
            => SuggestionSlotBuilder.Subtract(new SuggestionSlotBuilder.Span(start, end), claimed)
                .Where(piece => piece.Duration >= threshold)
                .Select(piece => new LostStretch(piece.Start, piece.End, what, claimable));

        var stretches = inactive
            .SelectMany(p => Unclaimed(p.Start, p.End, p.Reason, claimable: true))
            .Concat(blocks
                .Where(b => b.Classification.IsUnclassified)
                .SelectMany(b => Unclaimed(b.Block.Start, b.Block.End, $"uncategorized: “{b.Block.Title}”", claimable: false)))
            .OrderBy(s => s.Start)
            .ToList();

        return (stretches, TimeSpan.FromTicks(stretches.Sum(s => s.Duration.Ticks)));
    }

    // The lost-time list — and, in the live view, the place an idle/locked stretch is claimed:
    // name what it was, adjust the times if only part was real work, and Claim records a manual
    // timer over it. The stretch then bills like any timer and leaves this tab on the next refresh.
    private static void AppendLostTime(
        StringBuilder sb, IReadOnlyList<LostStretch> stretches, TimeSpan total, bool editable)
    {
        if (stretches.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing unaccounted for — every stretch over five minutes is either classified or on a timesheet line.</p>\n");
            return;
        }

        sb.Append($"<p class=\"hint\">{ReportFormat.Duration(total)} across {stretches.Count} {(stretches.Count == 1 ? "stretch" : "stretches")} that no timesheet line covers.</p>\n");

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n<tr><th>Start</th><th>End</th><th class=\"num\">Time</th><th>What happened</th>");
        if (editable)
            sb.Append("<th>What was it really?</th><th></th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var stretch in stretches)
        {
            var start = stretch.Start.ToLocalTime();
            var end = stretch.End.ToLocalTime();
            sb.Append("<tr class=\"lt\">");
            if (editable && stretch.Claimable)
            {
                sb.Append($"<td><input type=\"time\" class=\"lt-from\" value=\"{start:HH:mm}\" aria-label=\"Claim from\"></td>")
                  .Append($"<td><input type=\"time\" class=\"lt-to\" value=\"{end:HH:mm}\" aria-label=\"Claim to\"></td>");
            }
            else
            {
                sb.Append("<td>").Append(ReportFormat.Clock(stretch.Start)).Append("</td>")
                  .Append("<td>").Append(ReportFormat.Clock(stretch.End)).Append("</td>");
            }

            sb.Append("<td class=\"num\">").Append(ReportFormat.Duration(stretch.Duration)).Append("</td>")
              .Append("<td>").Append(Esc(stretch.What)).Append("</td>");
            if (editable)
            {
                sb.Append(stretch.Claimable
                    ? "<td><input class=\"lt-name\" type=\"text\" placeholder=\"e.g. Ticket #123 — phone call\" aria-label=\"What this time was\"></td>"
                      + "<td class=\"num\"><button class=\"uc-save lt-claim\" type=\"button\">Claim</button></td>"
                    : "<td class=\"muted\" colspan=\"2\">teach it a rule on the Uncategorized tab</td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        if (editable)
            sb.Append("<p class=\"hint\">Claiming records a manual timer over the stretch — it bills like any timer, shows on the Timers tab (delete it there to undo), and leaves this list. Trim the times first if only part of the stretch was real work.</p>\n");
    }

    // Window activity AND calls, merged into one time-ordered table so the Rollup is a complete
    // picture of the day. Calls carry the "Call" category badge; they overlay (don't replace) the
    // focused-window rows, so a call and its underlying window can both appear.
    private static void AppendRollup(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers, bool editable, IReadOnlyDictionary<string, string>? ticketOverrides,
        CategoryPalette? palette)
    {
        // One line per category, biggest first, holding its own activities. A day has far more
        // activities than categories, so the collapsed list is the answer to "where did the day
        // go" on its own — the detail is one click away when a total needs explaining.
        var groups = RollupBuilder.Build(blocks)
            .Concat(RollupBuilder.BuildCalls(calls, ticketOverrides))
            .Concat(RollupBuilder.BuildTimers(timers))
            .Where(r => r.Time >= RollupBuilder.MinRollupDuration)   // hide sub-minute noise
            .GroupBy(r => r.Category)
            .Select(g => new
            {
                Category = g.Key,
                // Summed from the rows that survived the sub-minute filter, so a category's total
                // is always exactly what expanding it shows.
                Total = TimeSpan.FromTicks(g.Sum(r => r.Time.Ticks)),
                Rows = g.OrderByDescending(r => r.Time)
                        .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
            })
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing to roll up yet — activity under a minute doesn't earn a row.</p>\n");
            return;
        }

        sb.Append("<p class=\"hint\">Each category is one line with its total; click it to see what made it up.</p>\n");
        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Category</th><th>App</th><th>Detail</th><th>Ticket</th><th class=\"num\">Time</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var group in groups)
        {
            // The category travels as base64 so a name with a quote or an ampersand in it can
            // still key its rows to their header.
            var key = B64(group.Category);
            sb.Append($"<tr class=\"rg\" data-cat=\"{key}\">")
              .Append("<td colspan=\"4\"><span class=\"rg-caret\">▸</span>")
              .Append(CategoryBadge(group.Category, palette))
              .Append($"<span class=\"rg-n\">{group.Rows.Count}</span></td>")
              .Append($"<td class=\"num\">{ReportFormat.Duration(group.Total)}</td></tr>\n");

            foreach (var row in group.Rows)
            {
                // The app always shows, categorized or not — a manual timer is the one row with no app.
                sb.Append($"<tr class=\"rgi\" data-cat=\"{key}\"><td class=\"rg-pad\"></td>")
                  .Append("<td>").Append(row.ProcessName is { } app ? Esc(app) : "<span class=\"muted\">—</span>").Append("</td>")
                  .Append("<td>").Append(Esc(ReportFormat.Detail(row.Client, row.DetailName))).Append("</td>")
                  .Append("<td>").Append(TicketCell(row, editable)).Append("</td>")
                  .Append("<td class=\"num\">").Append(ReportFormat.Duration(row.Time)).Append("</td></tr>\n");
            }
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    // In the live view, an activity row's Ticket cell is an editable input that saves a per-day
    // manual ticket (keyed by RowKey). Call rows (RowKey null) and the saved file report stay static.
    private static string TicketCell(RollupRow row, bool editable)
    {
        if (editable && row.RowKey is { } rowKey)
        {
            var key = B64(rowKey);
            var value = row.TicketRef is { } t ? Esc(t) : string.Empty;
            return $"<input class=\"tk\" type=\"text\" inputmode=\"numeric\" data-k=\"{key}\" value=\"{value}\" placeholder=\"—\" aria-label=\"Ticket number\">";
        }

        return row.TicketRef is { } tk ? $"#{Esc(tk)}" : string.Empty;
    }

    // The day by ticket: however many windows and apps a ticket's work crossed, it lands on one
    // row here — the "what did I actually touch today" list for time entry. Rows come from the
    // same effective ticket everything else bills by, so typing a ticket on a Rollup row files
    // that activity here too.
    private static void AppendTickets(StringBuilder sb, IReadOnlyList<TicketRow> tickets, CategoryPalette? palette)
    {
        if (tickets.Count == 0)
        {
            sb.Append("<p class=\"empty\">No tickets seen today. A window title carrying a ticket number files here automatically — typing a ticket on a Rollup row counts too.</p>\n");
            return;
        }

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Ticket</th><th>Category</th><th>App</th><th>Detail</th><th class=\"num\">Visits</th><th>First seen</th><th>Last seen</th><th class=\"num\">Time</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var ticket in tickets)
        {
            sb.Append("<tr><td>#").Append(Esc(ticket.TicketRef)).Append("</td>")
              .Append("<td>").Append(CategoryBadge(ticket.Category, palette)).Append("</td>")
              .Append("<td>").Append(Esc(ticket.ProcessName)).Append("</td>")
              .Append("<td>").Append(Esc(ticket.Detail)).Append("</td>")
              .Append("<td class=\"num\">").Append(ticket.Visits).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(ticket.FirstSeen)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(ticket.LastSeen)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(ticket.Time)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
        sb.Append("<p class=\"hint\">Each row sums every window that named the ticket. Visits are distinct sittings — the same count as the pins on that ticket's Timesheet block.</p>\n");
    }

    // Calls, Timeline, and the Rollup share one column shape — Category, App, Detail leading,
    // Time trailing — so the eye lands on the same facts in the same place on every tab.
    private static void AppendCalls(StringBuilder sb, IReadOnlyList<CallSpan> calls, CategoryPalette? palette)
    {
        if (calls.Count == 0)
        {
            sb.Append("<p class=\"empty\">No calls recorded today.</p>\n");
            return;
        }

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Category</th><th>App</th><th>Detail</th><th>Start</th><th>End</th><th class=\"num\">Time</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var call in calls)
        {
            // The category is the one the call's rollup row carries (Teams - Call, Discord, …),
            // so a call is filed identically wherever it shows.
            sb.Append("<tr><td>").Append(CategoryBadge(CallApps.CategoryFor(call.ProcessName), palette)).Append("</td>")
              .Append("<td>").Append(Esc(call.ProcessName)).Append("</td>")
              .Append("<td>").Append(Esc(call.Title)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(call.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(call.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(call.Duration)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void AppendTimeline(StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, CategoryPalette? palette)
    {
        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Category</th><th>App</th><th>Detail</th><th>Start</th><th>End</th><th class=\"num\">Time</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        // Newest first — most recent activity at the top.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            if (b.Classification.ExcludedFromTimeline)
                continue;

            // The URL rides as a hover on the Detail cell — visible when needed, no column spent.
            var urlTip = b.Block.Url is { } url ? $" title=\"{Esc(url)}\"" : string.Empty;
            // The Timeline keeps excluded activity — it is the record of what happened — but names
            // what it is missing from, otherwise a row absent from a total looks like a bug.
            var scope = b.Classification.ExcludeFrom;
            var excludedRow = scope is not ExcludeScope.None ? " class=\"tl-excluded\"" : string.Empty;
            sb.Append($"<tr{excludedRow}><td>").Append(CategoryBadge(b.Classification.Category, palette))
              .Append(ExcludeTag(scope))
              .Append("</td>")
              .Append("<td>").Append(Esc(b.Block.ProcessName)).Append("</td>")
              .Append($"<td{urlTip}>").Append(Esc(b.Block.Title)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(b.Block.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(b.Block.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(b.Block.Duration)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    /// <summary>The scopes an exclusion can name, in the order they're offered.</summary>
    private static readonly ExcludeScope[] ExcludableScopes =
        [ExcludeScope.Rollup, ExcludeScope.Timesheet, ExcludeScope.Timeline, ExcludeScope.All];

    // The control that decides where an activity counts, shared by the Rules tab (add bar and each
    // row) and the Uncategorized tab's triage, so the places offering the choice can't drift apart.
    //
    // It is two dropdowns rather than one list, because a single list of "Counted, Rollup,
    // Timesheet, Timeline, All" never says which of those mean *exclude* — the reader has to
    // already know. Choosing Include or Exclude first makes the sentence read itself, and the
    // second dropdown then only offers what that choice permits. The page renders the pair already
    // agreeing; the script only has to keep them agreeing as the first one changes.
    private static string ExcludeControls(string cssPrefix, ExcludeScope selected, string ariaLabel)
    {
        var excluding = selected is not ExcludeScope.None;
        var sb = new StringBuilder();

        sb.Append($"<select class=\"{cssPrefix}-mode ex-mode\" aria-label=\"{Esc(ariaLabel)}\">")
          .Append($"<option value=\"include\"{(excluding ? "" : " selected")}>Include</option>")
          .Append($"<option value=\"exclude\"{(excluding ? " selected" : "")}>Exclude</option>")
          .Append("</select>");

        sb.Append($"<select class=\"{cssPrefix} ex-scope\" aria-label=\"{Esc(ariaLabel)} scope\">");
        if (excluding)
        {
            foreach (var scope in ExcludableScopes)
            {
                var value = scope.ToString().ToLowerInvariant();
                sb.Append($"<option value=\"{value}\"{(scope == selected ? " selected" : "")}>{Esc(ExcludeScopeLabel(scope))}</option>");
            }
        }
        else
        {
            sb.Append("<option value=\"\" selected>Counted</option>");
        }

        return sb.Append("</select>").ToString();
    }

    // What a Timeline row says about being left out, named after the account it's missing from so
    // the row explains its own absence rather than just flagging it. Scopes that take the row off
    // the Timeline never reach here — there is no row left to tag.
    private static string ExcludeTag(ExcludeScope scope) => scope switch
    {
        ExcludeScope.Rollup => "<span class=\"tl-ex-tag\">not in rollup</span>",
        ExcludeScope.Timesheet => "<span class=\"tl-ex-tag\">not on timesheet</span>",
        _ => string.Empty,
    };

    /// <summary>How a scope is named wherever the user reads or picks one.</summary>
    internal static string ExcludeScopeLabel(ExcludeScope scope) => scope switch
    {
        ExcludeScope.Rollup => "Rollup",
        ExcludeScope.Timesheet => "Timesheet",
        ExcludeScope.Timeline => "Timeline",
        ExcludeScope.All => "All",
        _ => "—",
    };

    private static void Card(StringBuilder sb, string label, string value)
        => sb.Append($"<div class=\"card\"><div class=\"v\">{value}</div><div class=\"l\">{label}</div></div>\n");

    private static string CategoryBadge(string category, CategoryPalette? palette)
        => $"<span class=\"cat\" style=\"background:rgba({CategoryRgb(category, palette)},.22)\">{Esc(category)}</span>";

    /// <summary>The category the shipped Teams chat rule files a focused conversation under.</summary>
    private const string TeamsChatCategory = "Teams - Chat";

    // The hue a category is drawn in, as bare RGB so callers can pick their own alpha — a pill wants
    // a wash, a calendar block's edge wants the full colour. Text always uses the theme foreground,
    // so contrast holds in both themes. The user's own colours (categories.json) win over all of it.
    private static string CategoryRgb(string category, CategoryPalette? palette) => palette?.CustomRgb(category) ?? category switch
    {
        // Old category names ("HaloPSA", "Email", "Remote Support") stay as aliases: rules.json is
        // user-owned, so an installed copy may keep filing under them long after the defaults moved on.
        "Halo" or "HaloPSA" => "59,130,246",
        // Teams keeps one hue whether the time was a call or a chat — the badge text draws the
        // distinction, and the colour is there to say "this was Teams" at a glance.
        "Teams" or CallApps.TeamsCallCategory or TeamsChatCategory => "139,92,246",
        CallApps.DiscordCategory => "88,101,242",
        CallApps.RingCentralCategory => "6,182,212",
        "Outlook" or "Email" => "20,184,166",
        "Development" => "34,197,94",
        "IT Glue" => "239,68,68",
        "Browsing" => "234,179,8",
        "ScreenConnect" or "Remote Support" => "236,72,153",
        RollupBuilder.CallCategory => "249,115,22",
        RollupBuilder.TimerCategory => "99,102,241",
        // The one row that needs a human gets the same amber the gaps panel uses.
        SuggestionSlotBuilder.OddsAndEndsCategory => "214,158,46",
        _ => "148,163,184",
    };

    private static string Esc(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("\r", string.Empty)
        .Replace("\n", " ");

    private const string Css =
        """
        :root {
          --bg:#f6f7f9; --card:#fff; --fg:#1c2024; --muted:#626b75; --border:#e2e6ea;
          --accent:#0d8a78; --btn-fg:#fff; --btn-bg:#e4e8ec;
          --accent-weak:rgba(18,168,145,.12); --warn-bg:rgba(214,158,46,.12); --warn-border:rgba(214,158,46,.5);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg:#16181c; --card:#1e2126; --fg:#e6e9ec; --muted:#9aa4ae; --border:#2c3138;
            /* --btn-bg matches the WinForms InputBg the live window's own buttons rest on. */
            --accent:#2fd4b6; --btn-fg:#08201c; --btn-bg:#2a2e35;
            --accent-weak:rgba(47,212,182,.14); --warn-bg:rgba(214,158,46,.16); --warn-border:rgba(214,158,46,.55);
          }
        }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--fg);
          font:15px/1.5 -apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
        main { max-width:1080px; margin:0 auto; padding:32px 24px 64px; }
        .head { display:flex; justify-content:space-between; align-items:center; gap:16px; flex-wrap:wrap; }
        h1 { font-size:24px; margin:0 0 2px; }
        h1 .date { color:var(--muted); font-weight:500; font-size:18px; margin-left:8px; }
        h2 { font-size:13px; margin:32px 0 10px; letter-spacing:.05em; text-transform:uppercase; color:var(--muted); }
        .tracked { color:var(--muted); margin:0; }
        .cards { display:flex; flex-wrap:wrap; gap:12px; margin-top:16px; }
        .card { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:10px 16px; min-width:96px; }
        .card .v { font-size:20px; font-weight:600; font-variant-numeric:tabular-nums; }
        .card .l { font-size:11px; color:var(--muted); text-transform:uppercase; letter-spacing:.05em; }
        .scroll { overflow-x:auto; }
        table { width:100%; border-collapse:collapse; background:var(--card);
          border:1px solid var(--border); border-radius:10px; overflow:hidden; }
        th,td { text-align:left; padding:7px 12px; border-bottom:1px solid var(--border); }
        td { font-variant-numeric:tabular-nums; }
        th { font-size:11px; text-transform:uppercase; letter-spacing:.05em; color:var(--muted); font-weight:600; }
        th.num,td.num { text-align:right; white-space:nowrap; }
        tr:last-child td { border-bottom:none; }
        tbody tr:hover { background:var(--accent-weak); }
        .cat { display:inline-block; padding:1px 9px; border-radius:999px; font-size:12px; }
        .muted { color:var(--muted); }
        .gaps { background:var(--warn-bg); border:1px solid var(--warn-border); border-radius:10px; padding:4px 16px; }
        .gaps ul { margin:10px 0; padding-left:20px; }
        .gaps li { margin:4px 0; }
        .empty { color:var(--muted); }
        .tabs { display:flex; gap:2px; border-bottom:1px solid var(--border); margin:28px 0 16px; }
        .tab { background:none; border:none; border-bottom:2px solid transparent; color:var(--muted);
          font:inherit; font-size:12px; font-weight:600; text-transform:uppercase; letter-spacing:.05em;
          padding:9px 16px; margin-bottom:-1px; cursor:pointer; }
        .tab:hover { color:var(--fg); }
        .tab.active { color:var(--fg); border-bottom-color:var(--accent); }
        .panel { display:none; }
        .panel.active { display:block; }
        /* Every in-page button behaves like the app's own: a dark resting surface that lights up
           in the accent colour under the cursor, with dark text for contrast. Declared once so the
           three of them can't drift apart. */
        .tm-go,.tm-del,.uc-save { background:var(--btn-bg); color:var(--fg); border:none;
          border-radius:8px; font:inherit; font-weight:600; cursor:pointer; }
        .tm-go:hover,.tm-del:hover,.uc-save:hover { background:var(--accent); color:var(--btn-fg); }
        .tk,.tn { background:transparent; border:1px solid transparent; border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:2px 6px; }
        .tk { width:84px; }
        .tn { width:280px; max-width:100%; }
        .tk::placeholder { color:var(--muted); }
        .tk:hover,.tn:hover { border-color:var(--border); }
        .tk:focus,.tn:focus { outline:none; border-color:var(--accent); background:var(--bg); }
        .badge { display:inline-block; margin-left:7px; padding:0 7px; border-radius:999px;
          background:var(--warn-bg); border:1px solid var(--warn-border); color:var(--fg);
          font-size:11px; font-weight:600; text-transform:none; letter-spacing:0; }
        .hint { color:var(--muted); margin:0 0 12px; }
        .uc-cat,.uc-scope,.ex-mode,.ex-scope { background:var(--bg); border:1px solid var(--border);
          border-radius:6px; color:var(--fg); font:inherit; font-size:13px; padding:3px 6px; }
        .uc-cat { width:150px; }
        .uc-cat::placeholder { color:var(--muted); }
        .uc-cat:focus,.uc-scope:focus,.ex-mode:focus,.ex-scope:focus { outline:none; border-color:var(--accent); }
        /* Include/Exclude, then what it applies to — kept side by side so they read as one phrase. */
        .ex-mode { margin-right:5px; }
        .ex-pair { white-space:nowrap; }
        .uc-save { font-size:13px; padding:5px 12px; white-space:nowrap; }
        .uc-save:disabled { background:var(--border); color:var(--muted); cursor:default; }
        .uc-url { font-size:12px; word-break:break-all; }
        /* Rules tab: each row carries both its read view and its edit inputs; the row's
           "editing" class decides which shows. Patterns wrap so a long regex can't blow out
           the table. */
        .rules code { font-size:12px; word-break:break-all; }
        .rl .rl-in { display:none; }
        tr.rl.editing .rl-view { display:none; }
        tr.rl.editing .rl-in { display:inline-block; }
        .rl input.rl-in { background:var(--bg); border:1px solid var(--border); border-radius:6px;
          color:var(--fg); font:inherit; font-size:12px; padding:3px 6px; max-width:100%; }
        .rl input.rl-in:focus { outline:none; border-color:var(--accent); }
        .rl-cat { width:130px; }
        .rl-proc { width:130px; }
        .rl-title { width:200px; }
        .rl-url { width:180px; }
        .rl-client { width:100px; }
        .rl-ex-yes { color:var(--accent); font-weight:600; }
        .rl .ex-mode,.rl .ex-scope { font-size:12px; padding:3px 5px; }
        .rl-actions { white-space:nowrap; }
        .rl-actions .uc-save,.rl-actions .tm-del { font-size:12px; padding:4px 10px; }
        /* Rollup groups: a category line per row, its activities hidden until it's clicked. They
           start collapsed, so the tab opens as a short answer rather than a long list. */
        tr.rg { cursor:pointer; }
        tr.rg > td { font-weight:600; }
        tr.rg:hover > td { background:var(--btn-bg); }
        .rg-caret { display:inline-block; width:14px; color:var(--muted); font-size:11px;
          transition:transform .12s ease; }
        tr.rg.open .rg-caret { transform:rotate(90deg); }
        .rg-n { color:var(--muted); font-size:12px; font-weight:400; margin-left:8px; }
        tr.rgi { display:none; }
        tr.rgi.open { display:table-row; }
        .rg-pad { width:22px; }
        /* Timeline rows an excluding rule matched: still listed, visibly set apart, so a row that
           is missing from every total reads as deliberate rather than lost. */
        tr.tl-excluded { opacity:.55; }
        .tl-ex-tag { color:var(--muted); font-size:11px; margin-left:6px;
          text-transform:uppercase; letter-spacing:.04em; }
        /* Categories tab: the add bar on top, then a row per category with its colour swatch.
           Rows follow the Rules tab's view/edit toggle. */
        .ct-addbar { display:flex; align-items:center; gap:10px; margin:0 0 14px; }
        .ct-new-name { flex:0 1 260px; background:var(--bg); border:1px solid var(--border);
          border-radius:8px; color:var(--fg); font:inherit; font-size:14px; padding:6px 11px; }
        .ct-new-name::placeholder { color:var(--muted); }
        .ct-new-name:focus { outline:none; border-color:var(--accent); }
        .ct-add-btn { padding:7px 16px; }
        input[type=color] { width:36px; height:26px; padding:2px; background:var(--bg);
          border:1px solid var(--border); border-radius:6px; cursor:pointer; }
        .ct .ct-in { display:none; }
        tr.ct.editing .ct-view { display:none; }
        tr.ct.editing .ct-in { display:inline-block; }
        .ct input.ct-name { background:var(--bg); border:1px solid var(--border); border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:3px 6px; width:180px; max-width:100%; }
        .ct input.ct-name:focus { outline:none; border-color:var(--accent); }
        .ct-actions { white-space:nowrap; }
        .ct-actions .uc-save,.ct-actions .tm-del { font-size:12px; padding:4px 10px; }
        /* Rules tab's add bar, mirroring the categories one. */
        .rl-addbar { display:flex; flex-wrap:wrap; align-items:center; gap:8px; margin:0 0 14px; }
        .rl-addbar input { background:var(--bg); border:1px solid var(--border); border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:5px 8px; }
        .rl-addbar input::placeholder { color:var(--muted); }
        .rl-addbar input:focus { outline:none; border-color:var(--accent); }
        .rl-new-cat { width:140px; }
        .rl-new-proc { width:150px; }
        .rl-new-title { width:220px; }
        .rl-new-client { width:110px; }
        /* Settings tab: the dialog's layout, in page idiom. */
        .st-form { max-width:560px; }
        .st-row { display:flex; align-items:center; gap:10px; margin:8px 0; }
        .st-row label { flex:0 0 210px; color:var(--muted); }
        .st-hk { background:var(--bg); border:1px solid var(--border); border-radius:8px;
          color:var(--fg); font:inherit; font-size:14px; padding:6px 11px; width:180px;
          text-align:center; cursor:pointer; }
        .st-hk:focus { outline:none; border-color:var(--accent); }
        .st-times { display:flex; flex-wrap:wrap; gap:8px; margin:10px 0; min-height:20px; }
        .st-time { display:inline-flex; align-items:center; gap:6px; background:var(--btn-bg);
          border-radius:999px; padding:3px 6px 3px 12px; font-size:13px; }
        .st-time-del { background:none; border:none; color:var(--muted); font:inherit;
          font-size:14px; cursor:pointer; padding:0 4px; }
        .st-time-del:hover { color:#e05252; }
        input.st-time-new,input.st-ret { background:var(--bg); border:1px solid var(--border);
          border-radius:6px; color:var(--fg); font:inherit; font-size:13px; padding:4px 8px; }
        input.st-ret { width:70px; }
        input.st-ret:disabled { opacity:.5; }
        .st-forever { display:block; margin:6px 0 0; }
        .st-actions { display:flex; align-items:center; gap:14px; margin-top:22px; }
        .st-actions .tm-go { padding:8px 22px; }
        .st-msg { color:#ff8a8a; }
        .cal { position:relative; margin:14px 0 4px; }
        .cal-hr { position:absolute; left:0; right:0; border-top:1px solid var(--border); }
        .cal-hr.half { border-top-style:dotted; opacity:.55; }
        .cal-hr span { position:absolute; top:-8px; left:0; width:52px; text-align:right;
          padding-right:8px; background:var(--bg); color:var(--muted); font-size:11px; white-space:nowrap; }
        .cal-ev { position:absolute; left:60px; right:0; top:0; bottom:0; }
        .ev { position:absolute; box-sizing:border-box; overflow:hidden; border-radius:5px;
          border-left:3px solid; font-size:12px; line-height:17px; cursor:default; }
        .ev-fill { position:absolute; left:0; right:0; top:0; display:block; }
        .ev-pin { position:absolute; left:0; right:0; display:block; min-height:3px; }
        /* Lost-time claiming and the past-timer bar. */
        .lt input[type=time],.tm-pastbar input[type=time] { background:var(--bg);
          border:1px solid var(--border); border-radius:6px; color:var(--fg); font:inherit;
          font-size:13px; padding:3px 6px; }
        .lt-name { width:260px; max-width:100%; background:var(--bg); border:1px solid var(--border);
          border-radius:6px; color:var(--fg); font:inherit; font-size:13px; padding:3px 8px; }
        .lt-name::placeholder { color:var(--muted); }
        .lt-name:focus,.lt input[type=time]:focus,.tm-pastbar input:focus { outline:none; border-color:var(--accent); }
        .lt-claim { font-size:13px; padding:5px 12px; white-space:nowrap; }
        .tm-pastbar { display:flex; flex-wrap:wrap; align-items:center; gap:8px; margin:22px 0 8px; }
        .tm-past-name { flex:0 1 300px; background:var(--bg); border:1px solid var(--border);
          border-radius:8px; color:var(--fg); font:inherit; font-size:13px; padding:6px 11px; }
        .tm-past-name::placeholder { color:var(--muted); }
        .ev-txt { position:relative; display:block; padding:1px 8px;
          white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .ev b { font-variant-numeric:tabular-nums; margin-right:4px; }
        .ev:hover { filter:brightness(1.3); }
        .tmbar { display:flex; align-items:center; gap:10px; margin:18px 0 8px; flex-wrap:wrap; }
        .tm-name { flex:0 1 300px; background:var(--bg); border:1px solid var(--border); border-radius:8px;
          color:var(--fg); font:inherit; font-size:14px; padding:7px 11px; }
        .tm-name::placeholder { color:var(--muted); }
        .tm-name:focus { outline:none; border-color:var(--accent); }
        .tm-go { padding:8px 22px; }
        /* Stop stays dark and says so in red text, exactly as the app's own buttons do. */
        .tm-go.stop { color:#e05252; }
        .tm-elapsed { font-size:20px; font-weight:600; color:var(--accent); font-variant-numeric:tabular-nums; }
        .tm-del { font-size:12px; padding:4px 12px; }
        #export-json { padding:8px 16px; font-size:14px; }
        #xr { max-width:440px; border:1px solid var(--border); border-radius:12px; padding:22px 24px;
          background:var(--card); color:var(--fg); font:inherit; }
        #xr::backdrop { background:rgba(0,0,0,.45); }
        #xr h3 { margin:0 0 16px; font-size:15px; }
        #xr .hint { margin:10px 0 0; }
        .xr-all { display:block; margin-bottom:14px; }
        .xr-times { display:flex; align-items:center; gap:8px; }
        .xr-times label { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.05em; }
        .xr-times input { background:var(--bg); border:1px solid var(--border); border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:4px 8px; }
        .xr-times input:disabled { opacity:.5; }
        .xr-sum { margin:16px 0 0; font-weight:600; color:var(--accent); }
        .xr-sum.warn,.xr-warn { color:#d69e2e; }
        .xr-warn { display:none; }
        .xr-actions { display:flex; justify-content:flex-end; gap:10px; margin-top:20px; }
        .xr-actions button { padding:8px 18px; font-size:14px; }
        .tm-go:disabled { background:var(--border); color:var(--muted); cursor:default; }
        """;

    // Switches Rollup/Calls/Timeline tabs and survives live refreshes: the click listener is
    // delegated (so it outlives the innerHTML swap) and the chosen tab is stored on window and
    // re-applied by tallyApplyActiveTab after each swap.
    private const string TabScript =
        """
        (function(){
        function apply(){
        var name=window.__tallyTab||'rollup';var ok=false;
        document.querySelectorAll('.tab').forEach(function(t){var on=t.getAttribute('data-tab')===name;t.classList.toggle('active',on);if(on)ok=true;});
        if(!ok){name='rollup';window.__tallyTab='rollup';document.querySelectorAll('.tab').forEach(function(t){t.classList.toggle('active',t.getAttribute('data-tab')==='rollup');});}
        document.querySelectorAll('.panel').forEach(function(p){p.classList.toggle('active',p.getAttribute('data-panel')===name);});
        }
        window.tallyApplyActiveTab=apply;
        window.tallyShowTab=function(n){window.__tallyTab=n;apply();};
        document.addEventListener('click',function(e){var t=e.target.closest?e.target.closest('.tab'):null;if(!t)return;window.__tallyTab=t.getAttribute('data-tab');apply();});
        document.addEventListener('DOMContentLoaded',apply);
        })();
        """;

    // Keeps the Include/Exclude dropdown and the scope beside it agreeing. Include has exactly one
    // scope — "Counted" — so the second dropdown offers nothing to get wrong; choosing Exclude
    // fills it with the accounts an activity can be kept out of. The page renders the pair already
    // agreeing, so this only handles the user changing their mind. The scope select is found by
    // walking forward from the mode select rather than by container, because the three places that
    // use this pair sit in a div, a span, and a table cell.
    private const string ExcludeModeScript =
        """
        (function(){
        var SCOPES=[['rollup','Rollup'],['timesheet','Timesheet'],['timeline','Timeline'],['all','All']];
        document.addEventListener('change',function(e){
        var m=e.target;if(!m.classList||!m.classList.contains('ex-mode'))return;
        var s=m.nextElementSibling;
        while(s&&!(s.classList&&s.classList.contains('ex-scope')))s=s.nextElementSibling;
        if(!s)return;
        var keep=s.value;s.innerHTML='';
        if(m.value==='exclude'){
        for(var i=0;i<SCOPES.length;i++){
        var o=document.createElement('option');o.value=SCOPES[i][0];o.textContent=SCOPES[i][1];s.appendChild(o);}
        s.value=keep||SCOPES[0][0];
        if(!s.value)s.value=SCOPES[0][0];}
        else{var n=document.createElement('option');n.value='';n.textContent='Counted';s.appendChild(n);s.value='';}});
        })();
        """;

    // Expands and collapses the Rollup's category groups. Which ones are open lives on window,
    // not in the DOM, so a live refresh that replaces the whole table doesn't slam them shut
    // mid-read; the listener is delegated for the same reason. Everything starts collapsed —
    // that's the CSS default, and an unopened category simply isn't in the set.
    private const string RollupGroupScript =
        """
        (function(){
        function open(){return window.__tallyRollupOpen||(window.__tallyRollupOpen={});}
        function apply(){var o=open();
        document.querySelectorAll('tr.rg').forEach(function(h){
        h.classList.toggle('open',!!o[h.getAttribute('data-cat')]);});
        document.querySelectorAll('tr.rgi').forEach(function(r){
        r.classList.toggle('open',!!o[r.getAttribute('data-cat')]);});}
        window.tallyApplyRollupGroups=apply;
        document.addEventListener('click',function(e){
        var h=e.target.closest?e.target.closest('tr.rg'):null;if(!h)return;
        var c=h.getAttribute('data-cat');var o=open();
        if(o[c]){delete o[c];}else{o[c]=1;}
        apply();});
        document.addEventListener('DOMContentLoaded',apply);
        })();
        """;

    // Swaps fresh <main> content in without a reload, keeping scroll steady and the selected tab.
    // Skips the swap whenever a field is focused (a ticket, a timer name, a triage category), a
    // rules/categories row is in edit mode, an add bar holds half-typed text, or the settings
    // form has unsaved changes — even unfocused, those inputs hold state a swap would erase — so
    // a refresh never wipes an in-progress edit; the next tick lands once the edit concludes.
    private const string LiveUpdateScript =
        """
        window.tallyUpdate=function(h){
        var a=document.activeElement;if(a&&(a.tagName==='INPUT'||a.tagName==='SELECT'))return;
        if(document.querySelector('tr.rl.editing,tr.ct.editing,.st-form.st-dirty'))return;
        var ab=document.querySelectorAll('.ct-new-name,.rl-addbar input[type=text],.lt-name,.tm-past-name');
        for(var i=0;i<ab.length;i++){if(ab[i].value)return;}
        var y=window.scrollY;var m=document.getElementById('tally-live');
        if(m){m.innerHTML=h;if(window.tallyApplyActiveTab){window.tallyApplyActiveTab();}
        if(window.tallyApplyRollupGroups){window.tallyApplyRollupGroups();}window.scrollTo(0,y);}};
        """;

    // Editable cells: a ticket cell (.tk) posts {type:'ticket', key, value}; a timer-name cell (.tn)
    // posts {type:'timerName', id, value}. Committed on blur or Enter (which blurs). Delegated so the
    // listeners survive the innerHTML swaps; the C# host saves the change and refreshes.
    private const string TicketEditScript =
        """
        (function(){
        function post(i){if(!window.chrome||!window.chrome.webview)return;
        if(i.classList.contains('tk'))window.chrome.webview.postMessage({type:'ticket',key:i.getAttribute('data-k'),value:i.value});
        else if(i.classList.contains('tn'))window.chrome.webview.postMessage({type:'timerName',id:i.getAttribute('data-timer-id'),value:i.value});}
        function isEdit(t){return t&&t.classList&&(t.classList.contains('tk')||t.classList.contains('tn'));}
        document.addEventListener('change',function(e){if(isEdit(e.target))post(e.target);});
        document.addEventListener('keydown',function(e){if(e.key==='Enter'&&isEdit(e.target)){e.preventDefault();e.target.blur();}});
        })();
        """;

    /// <summary>
    /// The saved report's Export timesheet button: opens the range dialog, then writes the filtered
    /// document client-side from the embedded copy — no app, no server, works offline.
    /// <para>
    /// The window is compared against the wall clock the producer wrote into each slot, read
    /// straight out of the ISO string rather than through Date. Parsing would re-express the time
    /// in whatever zone the reader happens to be in, and "the morning" has to mean the morning of
    /// the machine that recorded it.
    /// </para>
    /// </summary>
    private const string SnapshotExportScript =
        """
        (function(){
        var raw=document.getElementById('tally-export');
        var btn=document.getElementById('export-json');
        if(!raw||!btn)return;
        var doc=JSON.parse(raw.textContent);
        var slots=doc.slots||[];
        if(!slots.length){btn.style.display='none';return;}
        var dlg=document.getElementById('xr'),every=document.getElementById('xr-every'),
        from=document.getElementById('xr-from'),to=document.getElementById('xr-to'),
        sum=document.getElementById('xr-sum'),warn=document.getElementById('xr-warn'),
        go=document.getElementById('xr-go'),cancel=document.getElementById('xr-cancel');
        function mins(iso){return parseInt(iso.substr(11,2),10)*60+parseInt(iso.substr(14,2),10);}
        function hhmm(m){var h=Math.floor(m/60),x=m%60;return (h<10?'0':'')+h+':'+(x<10?'0':'')+x;}
        function val(el){var p=/^(\d{2}):(\d{2})$/.exec(el.value);return p?parseInt(p[1],10)*60+parseInt(p[2],10):null;}
        var starts=slots.map(function(s){return mins(s.start);});
        from.value=hhmm(Math.min.apply(null,starts));
        to.value=hhmm(Math.max.apply(null,slots.map(function(s){return mins(s.end);})));
        function chosen(){
        if(every.checked)return slots.slice();
        var a=val(from),b=val(to);
        return slots.filter(function(s){var m=mins(s.start);return (a===null||m>=a)&&(b===null||m<b);});}
        function sync(){
        var custom=!every.checked;
        from.disabled=to.disabled=!custom;
        warn.style.display=custom?'block':'none';
        var pick=chosen();
        var hours=pick.reduce(function(t,s){return t+(s.hours||0);},0);
        sum.textContent=pick.length?pick.length+(pick.length===1?' entry · ':' entries · ')+hours.toFixed(2)+' h'
        :'Nothing starts inside that window.';
        sum.className=pick.length?'xr-sum':'xr-sum warn';
        go.disabled=!pick.length;}
        [every,from,to].forEach(function(el){el.addEventListener('change',sync);el.addEventListener('input',sync);});
        btn.addEventListener('click',function(){sync();dlg.showModal();});
        cancel.addEventListener('click',function(){dlg.close();});
        go.addEventListener('click',function(){
        var pick=chosen();if(!pick.length)return;
        var out={schema_version:doc.schema_version,source:doc.source,range:doc.range,slots:pick};
        var name='tally-'+btn.getAttribute('data-date')
        +(every.checked?'':'-'+from.value.replace(':','')+'-'+to.value.replace(':',''))+'.json';
        var u=URL.createObjectURL(new Blob([JSON.stringify(out,null,2)],{type:'application/json'}));
        var a=document.createElement('a');a.href=u;a.download=name;
        document.body.appendChild(a);a.click();document.body.removeChild(a);
        URL.revokeObjectURL(u);dlg.close();});
        })();
        """;

    // The Timers tab's start/stop control. Start/Stop posts {type:'timerToggle', value:<name>};
    // editing the name posts {type:'timerRename', value}. The host owns the timer, so the button's
    // new state arrives with the refresh that follows rather than being guessed here. Between
    // refreshes the elapsed figure ticks locally from data-started, matching TimerText.Elapsed.
    // Claiming a lost stretch and adding a past timer both post {type:'timerAdd', start, stop,
    // value:<name>} — the host records the timer and re-validates everything.
    private const string TimerControlScript =
        """
        (function(){
        function post(m){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}
        function name(){var n=document.querySelector('.tm-name');return n?n.value:'';}
        function claim(scope,fromSel,toSel,nameSel){
        var v=function(s){var i=scope.querySelector(s);return i?i.value.trim():'';};
        var n=v(nameSel);
        if(!n){var f=scope.querySelector(nameSel);if(f)f.focus();return;}
        var from=v(fromSel),to=v(toSel);
        if(!/^\d{2}:\d{2}$/.test(from)||!/^\d{2}:\d{2}$/.test(to)||to<=from)return;
        post({type:'timerAdd',start:from,stop:to,value:n});
        var f2=scope.querySelector(nameSel);if(f2)f2.value='';}
        document.addEventListener('click',function(e){
        var t=e.target;if(!t.closest)return;
        var c=t.closest('.lt-claim');
        if(c){var r=c.closest('tr.lt');if(r)claim(r,'.lt-from','.lt-to','.lt-name');return;}
        var p=t.closest('.tm-past-add');
        if(p){var bar=p.closest('.tm-pastbar');if(bar)claim(bar,'.tm-past-from','.tm-past-to','.tm-past-name');return;}
        var b=t.closest('.tm-go');
        if(b){post({type:'timerToggle',value:name()});return;}
        var d=t.closest('.tm-del');
        if(d)post({type:'timerDelete',id:d.getAttribute('data-timer-id')});});
        document.addEventListener('change',function(e){
        if(e.target.classList&&e.target.classList.contains('tm-name'))post({type:'timerRename',value:e.target.value});});
        document.addEventListener('keydown',function(e){
        if(e.key==='Enter'&&e.target.classList&&e.target.classList.contains('tm-name')){
        e.preventDefault();var v=e.target.value;e.target.blur();post({type:'timerToggle',value:v});}});
        function pad(v){return v<10?'0'+v:''+v;}
        setInterval(function(){
        var el=document.querySelector('.tm-elapsed[data-started]');if(!el)return;
        var s=Date.parse(el.getAttribute('data-started'));if(isNaN(s))return;
        var d=Math.max(0,Math.floor((Date.now()-s)/1000));
        var h=Math.floor(d/3600),m=Math.floor(d/60)%60,x=d%60;
        el.textContent=h>0?h+':'+pad(m)+':'+pad(x):pad(m)+':'+pad(x);},1000);
        })();
        """;

    // The Rules tab. Edit toggles a row into its input view (the row's data never leaves the DOM);
    // Save posts {type:'ruleUpdate', id:<index>, key:<b64 rule id>, category, process, title,
    // client, exclude}
    // with the typed values plain (they ride the JSON message, not an attribute). Delete posts
    // {type:'ruleDelete', id, key} and the host asks for confirmation before touching the file. The
    // index says which rule, the id proves the table wasn't stale — the host checks both.
    // The branches share one click handler, so a `var` in any of them is hoisted across all of
    // them: the add-rule branch's field reader is `newVal` because naming it `val` shadowed the
    // row reader above and left Save throwing before it could post anything.
    private const string RulesEditScript =
        """
        (function(){
        function post(m){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}
        function val(r,c){var i=r.querySelector('input.'+c);return i?i.value:'';}
        function sel(r,c){var i=r.querySelector('select.'+c);return i?i.value:'';}
        document.addEventListener('click',function(e){
        var t=e.target;if(!t.closest)return;
        var b=t.closest('.rl-edit');
        if(b){var r=b.closest('tr.rl');r.classList.add('editing');var c=r.querySelector('.rl-cat');if(c)c.focus();return;}
        b=t.closest('.rl-cancel');
        if(b){b.closest('tr.rl').classList.remove('editing');return;}
        b=t.closest('.rl-add-btn');
        if(b){var bar=b.closest('.rl-addbar');if(!bar)return;
        var newVal=function(c){var i=bar.querySelector(c);return i?i.value.trim():'';};
        var cat=newVal('.rl-new-cat');
        if(!cat){var f=bar.querySelector('.rl-new-cat');if(f)f.focus();return;}
        var proc=newVal('.rl-new-proc'),ti=newVal('.rl-new-title'),ur=newVal('.rl-new-url');
        if(!proc&&!ti&&!ur){var f2=bar.querySelector('.rl-new-proc');if(f2)f2.focus();return;}
        post({type:'ruleAdd',category:cat,process:proc,title:ti,url:ur,
        client:newVal('.rl-new-client'),excludeFrom:sel(bar,'rl-new-exclude')});
        bar.querySelectorAll('input').forEach(function(i){i.value='';});
        bar.querySelectorAll('select').forEach(function(s){s.selectedIndex=0;});
        // Resetting the mode select back to Include leaves the scope beside it still listing
        // exclusions, so tell it the mode changed and let one place rebuild the pair.
        var md=bar.querySelector('.ex-mode');
        if(md)md.dispatchEvent(new Event('change',{bubbles:true}));
        return;}
        b=t.closest('.rl-del');
        if(b){var r=b.closest('tr.rl');post({type:'ruleDelete',id:r.getAttribute('data-i'),key:r.getAttribute('data-id')});return;}
        b=t.closest('.rl-ok');
        if(b){var r=b.closest('tr.rl');
        post({type:'ruleUpdate',id:r.getAttribute('data-i'),key:r.getAttribute('data-id'),
        category:val(r,'rl-cat'),process:val(r,'rl-proc'),title:val(r,'rl-title'),url:val(r,'rl-url'),
        client:val(r,'rl-client'),excludeFrom:sel(r,'rl-exclude')});
        r.classList.remove('editing');}});
        document.addEventListener('keydown',function(e){
        var r=e.target.closest?e.target.closest('tr.rl.editing'):null;if(!r)return;
        if(e.key==='Enter'){e.preventDefault();var b=r.querySelector('.rl-ok');if(b)b.click();}
        else if(e.key==='Escape'){r.classList.remove('editing');}});
        })();
        """;

    // The Categories tab. Add posts {type:'catAdd', category, value:<hex>}; changing a swatch posts
    // {type:'catColor', key:<b64 name>, value:<hex>} on commit; Rename follows the rules-row edit
    // toggle and posts {type:'catRename', key:<b64 old name>, category:<new name>}; Delete posts
    // {type:'catDelete', key} and the host confirms before touching the file.
    private const string CategoriesScript =
        """
        (function(){
        function post(m){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}
        document.addEventListener('click',function(e){
        var t=e.target;if(!t.closest)return;
        var b=t.closest('.ct-add-btn');
        if(b){var n=document.querySelector('.ct-new-name'),c=document.querySelector('.ct-new-color');
        var v=n?n.value.trim():'';if(!v){if(n)n.focus();return;}
        post({type:'catAdd',category:v,value:c?c.value:''});if(n)n.value='';return;}
        b=t.closest('.ct-rename');
        if(b){var r=b.closest('tr.ct');r.classList.add('editing');var i=r.querySelector('.ct-name');if(i)i.focus();return;}
        b=t.closest('.ct-cancel');
        if(b){b.closest('tr.ct').classList.remove('editing');return;}
        b=t.closest('.ct-del');
        if(b){var r=b.closest('tr.ct');post({type:'catDelete',key:r.getAttribute('data-name')});return;}
        b=t.closest('.ct-ok');
        if(b){var r=b.closest('tr.ct');var i=r.querySelector('.ct-name');
        post({type:'catRename',key:r.getAttribute('data-name'),category:i?i.value:''});
        r.classList.remove('editing');}});
        document.addEventListener('change',function(e){
        var el=e.target;if(!el.classList||!el.classList.contains('ct-color'))return;
        var r=el.closest('tr.ct');if(r)post({type:'catColor',key:r.getAttribute('data-name'),value:el.value});});
        document.addEventListener('keydown',function(e){
        var r=e.target.closest?e.target.closest('tr.ct.editing'):null;
        if(r){if(e.key==='Enter'){e.preventDefault();var b=r.querySelector('.ct-ok');if(b)b.click();}
        else if(e.key==='Escape'){r.classList.remove('editing');}return;}
        if(e.key==='Enter'&&e.target.classList&&e.target.classList.contains('ct-new-name')){
        e.preventDefault();var b=document.querySelector('.ct-add-btn');if(b)b.click();}});
        })();
        """;

    // The Settings tab. Hotkey fields capture the next Ctrl/Alt/Shift+key combo pressed while
    // focused; times are managed as removable chips; Save posts the whole form at once as
    // {type:'settingsSave', start, stop, times:<comma-joined HH:mm>, retention:<days, 0=forever>}.
    // The st-dirty class marks unsaved edits so live refreshes leave the form alone.
    private const string SettingsScript =
        """
        (function(){
        function post(m){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}
        function form(){return document.querySelector('.st-form');}
        function dirty(){var f=form();if(f)f.classList.add('st-dirty');}
        document.addEventListener('focusin',function(e){var t=e.target;
        if(t.classList&&t.classList.contains('st-hk')){t.dataset.prev=t.value;t.value='Press keys…';}});
        document.addEventListener('focusout',function(e){var t=e.target;
        if(t.classList&&t.classList.contains('st-hk')&&t.value==='Press keys…')t.value=t.dataset.prev||'';});
        document.addEventListener('keydown',function(e){var t=e.target;
        if(!t.classList||!t.classList.contains('st-hk'))return;
        if(e.key==='Tab'||e.key==='Escape')return;
        e.preventDefault();
        var key=null;
        if(/^[a-z0-9]$/i.test(e.key))key=e.key.toUpperCase();
        else if(/^F([1-9]|1[0-9]|2[0-4])$/.test(e.key))key=e.key;
        if(!key)return;
        var parts=[];if(e.ctrlKey)parts.push('Ctrl');if(e.altKey)parts.push('Alt');if(e.shiftKey)parts.push('Shift');
        if(!parts.length)return;
        parts.push(key);t.value=parts.join('+');t.dataset.prev=t.value;dirty();});
        document.addEventListener('click',function(e){var t=e.target;if(!t.closest)return;
        var b=t.closest('.st-time-add');
        if(b){var i=document.querySelector('.st-time-new');var v=i?i.value:'';
        if(!/^\d{2}:\d{2}$/.test(v))return;
        var list=document.querySelector('.st-times');if(!list)return;
        var exists=false;list.querySelectorAll('.st-time').forEach(function(c){if(c.getAttribute('data-t')===v)exists=true;});
        if(exists)return;
        var h=parseInt(v.slice(0,2),10),m=v.slice(3,5);
        var span=document.createElement('span');span.className='st-time';span.setAttribute('data-t',v);
        span.append(((h%12)||12)+':'+m+' '+(h<12?'AM':'PM'));
        var x=document.createElement('button');x.type='button';x.className='st-time-del';x.textContent='×';
        span.append(x);list.append(span);dirty();return;}
        var d=t.closest('.st-time-del');
        if(d){var c=d.closest('.st-time');if(c){c.remove();dirty();}return;}
        var s=t.closest('.st-save');
        if(s){var f=form();if(!f)return;
        var hk=f.querySelectorAll('.st-hk');
        var read=function(el){return !el?'':el.value==='Press keys…'?(el.dataset.prev||''):el.value;};
        var start=read(hk[0]),stop=read(hk[1]);
        var msg=f.querySelector('.st-msg');
        if(!start||!stop){if(msg)msg.textContent='Each hotkey needs a combination.';return;}
        if(start.toLowerCase()===stop.toLowerCase()){if(msg)msg.textContent='Start and stop must be different.';return;}
        var times=[];f.querySelectorAll('.st-time').forEach(function(c){times.push(c.getAttribute('data-t'));});
        var forever=f.querySelector('.st-keep-forever');
        var ret=forever&&forever.checked?'0':((f.querySelector('.st-ret')||{}).value||'90');
        post({type:'settingsSave',start:start,stop:stop,times:times.join(','),retention:ret});
        f.classList.remove('st-dirty');if(msg)msg.textContent='';}});
        document.addEventListener('change',function(e){var t=e.target;if(!t.closest||!t.closest('.st-form'))return;
        if(t.classList.contains('st-keep-forever')){var r=document.querySelector('.st-ret');if(r)r.disabled=t.checked;}
        dirty();});
        })();
        """;

    // "Save rule" in the Unclassified tab: posts {type:'rule', process, title, scope, category,
    // excludeFrom} to the host, which writes it into rules.json. The app + window travel as the
    // base64 the row carries, so nothing is re-derived from display text. An empty category
    // focuses the field instead of posting — unless an exclusion was chosen, which is a complete
    // decision on its own and lets the host name the category.
    // This handler sees every .uc-save button on the page, the Rules tab's included; those rows
    // carry no .uc-cat, so they fall out here and their own script handles them.
    private const string RuleSaveScript =
        """
        (function(){
        document.addEventListener('click',function(e){
        var b=e.target.closest?e.target.closest('.uc-save'):null;if(!b||b.disabled)return;
        var r=b.closest('tr');if(!r)return;
        var c=r.querySelector('.uc-cat');var s=r.querySelector('.uc-scope');
        var x=r.querySelector('.uc-exclude');var ex=x?x.value:'';
        var v=c?c.value.trim():'';
        if(!v&&!ex){if(c)c.focus();return;}
        if(!window.chrome||!window.chrome.webview)return;
        b.disabled=true;b.textContent='Saved';
        window.chrome.webview.postMessage({type:'rule',process:r.getAttribute('data-p'),title:r.getAttribute('data-t'),url:r.getAttribute('data-u'),scope:s?s.value:'app',category:v,excludeFrom:ex});});
        })();
        """;

}
