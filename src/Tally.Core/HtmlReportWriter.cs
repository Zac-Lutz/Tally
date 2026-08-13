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
        string? embeddedJson = null)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Tally — {ReportFormat.DisplayDate(date)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timers ?? [],
            gapThreshold ?? TimeSpan.FromMinutes(5), includeHeader: true, editable: false,
            ticketOverrides: ticketOverrides, timerPanel: null, showExport: embeddedJson is not null);

        sb.Append("</main>\n");
        sb.Append("<script>").Append(TabScript).Append("</script>\n");

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
        TimerPanelState? timerPanel = null)
    {
        var sb = new StringBuilder();
        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timers ?? [],
            gapThreshold ?? TimeSpan.FromMinutes(5), includeHeader: false, editable: true,
            ticketOverrides: ticketOverrides, timerPanel: timerPanel);
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
        sb.Append("<script>").Append(LiveUpdateScript).Append("</script>\n");
        sb.Append("<script>").Append(TicketEditScript).Append("</script>\n");
        sb.Append("<script>").Append(RuleSaveScript).Append("</script>\n");
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
        bool showExport = false)
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

        AppendSummary(sb, blocks, calls, inactivePeriods);
        AppendTabs(sb, blocks, calls, inactivePeriods, timers, threshold, editable, ticketOverrides, timerPanel);
    }

    // Rollup / Calls / Timeline / Timers / Unclassified as switchable tabs (Rollup active by default)
    // instead of stacked sections. Tab switching + preserving the choice across live refreshes is
    // TabScript. The Unclassified tab carries a count so a day needing triage announces itself.
    private static void AppendTabs(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods, IReadOnlyList<ManualTimer> timers,
        TimeSpan threshold, bool editable, IReadOnlyDictionary<string, string>? ticketOverrides,
        TimerPanelState? timerPanel)
    {
        var unclassified = UnclassifiedBuilder.Build(blocks);
        var (lostLines, lostTotal) = LostTime(blocks, inactivePeriods, threshold);

        sb.Append("<div class=\"tabs\">");
        sb.Append("<button class=\"tab active\" type=\"button\" data-tab=\"rollup\">Rollup</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timesheet\">Timesheet</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timeline\">Timeline</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"calls\">Calls</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timers\">Timers</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"unclassified\">Unclassified");
        if (unclassified.Count > 0)
            sb.Append($"<span class=\"badge\">{unclassified.Count}</span>");
        sb.Append("</button>");
        // The badge is the total, not a count: how much time is unaccounted for is the question,
        // and a "6" tells you nothing about whether that's six minutes or six hours.
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"lost\">Lost time");
        if (lostLines.Count > 0)
            sb.Append($"<span class=\"badge\">{ReportFormat.Duration(lostTotal)}</span>");
        sb.Append("</button>");
        sb.Append("</div>\n");

        // Always the whole day: choosing a slice belongs to the export itself, so this stays the
        // one honest picture of what happened rather than a filtered one.
        sb.Append("<section class=\"panel\" data-panel=\"timesheet\">\n");
        AppendTimesheet(sb, SuggestionSlotBuilder.Build(blocks, calls, timers));
        sb.Append("</section>\n");

        sb.Append("<section class=\"panel active\" data-panel=\"rollup\">\n");
        AppendRollup(sb, blocks, calls, timers, editable, ticketOverrides);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timeline\">\n");
        AppendTimeline(sb, blocks);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"calls\">\n");
        AppendCalls(sb, calls);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timers\">\n");
        AppendTimers(sb, timers, editable, timerPanel);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"unclassified\">\n");
        AppendUnclassified(sb, unclassified, blocks, editable);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"lost\">\n");
        AppendLostTime(sb, lostLines, lostTotal);
        sb.Append("</section>\n");
    }

    /// <summary>
    /// The timesheet preview: exactly the entries the JSON export will contain, so what uploads can
    /// be checked before it's uploaded. Measured time is shown beside the reported figure — the
    /// rounding is visible rather than something the file does quietly.
    /// </summary>
    private static void AppendTimesheet(StringBuilder sb, IReadOnlyList<SuggestionSlot> slots)
    {
        if (slots.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing to put on a timesheet yet.</p>\n");
            return;
        }

        var total = slots.Sum(s => s.Reported.TotalHours);
        var measured = TimeSpan.FromTicks(slots.Sum(s => s.Measured.Ticks));
        sb.Append($"<p class=\"hint\">{slots.Count} {(slots.Count == 1 ? "entry" : "entries")} · <strong>{total:0.00} h</strong> to enter · {ReportFormat.Duration(measured)} actually measured. This is exactly what the export contains.</p>\n");

        AppendCalendar(sb, slots);
        sb.Append("<p class=\"hint\">Blocks sit where the work happened and are as tall as the time they span; the number on each is the hours to enter. Time is claimed once — a timer beats a meeting, a meeting beats whatever window was open during it. Anything too short to stand alone is gathered into the “odds and ends” block rather than dropped.</p>\n");
    }

    /// <summary>How many pixels one minute of the day is drawn as.</summary>
    private const double MinuteHeight = 1.35;

    /// <summary>Floor height for a block, so a five-minute entry still shows its label.</summary>
    private const double MinEventHeight = 19;

    // The day as a calendar grid: an hour ruler down the left and each slot placed against it.
    // Blocks are drawn over their real span (the shape att's own calendar will show them in), with
    // the billable hours on the block — the two differ whenever short gaps were bridged, and the
    // gaps between blocks are the point: unaccounted time is visible as empty space.
    private static void AppendCalendar(StringBuilder sb, IReadOnlyList<SuggestionSlot> slots)
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
            var span = slot.End - slot.Start;
            var top = (slot.Start - bounds.Start).TotalMinutes * MinuteHeight;
            var height = Math.Max(MinEventHeight, span.TotalMinutes * MinuteHeight);
            var width = 100d / entry.Columns;
            var left = entry.Column * width;

            var rgb = CategoryRgb(slot.Category);
            var ticket = slot.TicketRef is { } t ? $"#{t} " : string.Empty;
            var tip = $"{ReportFormat.Clock(slot.Start)}–{ReportFormat.Clock(slot.End)} · {ticket}{slot.Label} · "
                      + $"{ReportFormat.Duration(slot.Measured)} measured → {slot.Reported.TotalHours:0.00} h to enter";

            sb.Append($"<div class=\"ev\" style=\"top:{Px(top)}px;height:{Px(height - 2)}px;")
              .Append($"left:calc({Px(left)}% + 1px);width:calc({Px(width)}% - 3px);")
              .Append($"background:rgba({rgb},.10);border-left-color:rgba({rgb},.9)\" title=\"{Esc(tip)}\">");

            // Blocks are drawn over the stretch they cover, but the billable time can be less when
            // short gaps were bridged. The solid part is the time actually measured, so a block
            // that's mostly empty reads as mostly empty instead of as a full hour of work.
            var fill = span > TimeSpan.Zero
                ? Math.Clamp(slot.Measured.TotalMinutes / span.TotalMinutes * 100, 0, 100)
                : 100;
            sb.Append($"<i class=\"ev-fill\" style=\"height:{Px(fill)}%;background:rgba({rgb},.22)\"></i>");

            sb.Append("<span class=\"ev-txt\">")
              .Append($"<b>{slot.Reported.TotalHours:0.00}</b> ")
              .Append(Esc(ticket + slot.Label))
              .Append("</span></div>\n");
        }

        sb.Append("</div>\n</div>\n");
    }

    private static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // The triage list: everything that matched no rule, one row per app+window. In the live view each
    // row can be given a category and saved as a rule on the spot (the C# host writes rules.json and
    // the next refresh reclassifies the day). The saved file report lists them read-only.
    private static void AppendUnclassified(
        StringBuilder sb, IReadOnlyList<UnclassifiedRow> rows, IReadOnlyList<ClassifiedBlock> blocks, bool editable)
    {
        if (rows.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing unclassified — every activity today matched a rule.</p>\n");
            return;
        }

        if (editable)
            sb.Append("<p class=\"hint\">Give an activity a category and save it as a rule. It applies to today straight away, and to every day from here.</p>\n");

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n<tr><th>App</th><th>Window</th><th class=\"num\">Time</th>");
        if (editable)
            sb.Append("<th>Category</th><th>Applies to</th><th></th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var row in rows)
        {
            sb.Append($"<tr data-p=\"{B64(row.ProcessName)}\" data-t=\"{B64(row.Title)}\">")
              .Append("<td>").Append(Esc(row.ProcessName)).Append("</td>")
              .Append("<td>").Append(Esc(row.Title)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(row.Time)).Append("</td>");
            if (editable)
            {
                sb.Append("<td><input class=\"uc-cat\" type=\"text\" list=\"uc-cats\" placeholder=\"Category\" aria-label=\"Category\"></td>")
                  .Append("<td><select class=\"uc-scope\" aria-label=\"Applies to\">")
                  .Append($"<option value=\"app\">Any {Esc(row.ProcessName)} window</option>")
                  .Append("<option value=\"window\">Only this window</option></select></td>")
                  .Append("<td class=\"num\"><button class=\"uc-save\" type=\"button\">Save rule</button></td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");

        if (!editable)
            return;

        // Suggests the categories already in use so the day's naming stays consistent (free text
        // still wins — a datalist only proposes).
        sb.Append("<datalist id=\"uc-cats\">");
        foreach (var category in KnownCategories(blocks))
            sb.Append($"<option value=\"{Esc(category)}\"></option>");
        sb.Append("</datalist>\n");
    }

    // Categories seen today, plus the shipped defaults so a fresh day still offers sensible names.
    private static readonly string[] BaselineCategories =
    [
        "Admin", "Browsing", "Development", "Discord", "Email", "HaloPSA", "Meetings",
        "Remote Support", "RingCentral", "Teams",
    ];

    private static IReadOnlyList<string> KnownCategories(IReadOnlyList<ClassifiedBlock> blocks)
        => blocks
            .Select(b => b.Classification.Category)
            .Where(c => c != Classification.Unclassified)
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
    }

    private static void AppendSummary(
        StringBuilder sb,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactive)
    {
        var active = TimeSpan.FromTicks(blocks.Sum(b => b.Block.Duration.Ticks));
        var callTime = TimeSpan.FromTicks(calls.Sum(c => c.Duration.Ticks));
        var inactiveTime = TimeSpan.FromTicks(inactive.Sum(p => p.Duration.Ticks));

        if (blocks.Count > 0 || calls.Count > 0)
        {
            var first = blocks.Count > 0 ? blocks[0].Block.Start : calls[0].Start;
            var last = blocks.Count > 0 ? blocks[^1].Block.End : calls[^1].End;
            sb.Append($"<p class=\"tracked\">Tracked {ReportFormat.Clock(first)}–{ReportFormat.Clock(last)}</p>\n");
        }

        // Total = all recorded wall-clock (active work + idle/locked). Calls and manual timers
        // overlay that time rather than adding to it, so they're not summed in.
        sb.Append("<div class=\"cards\">\n");
        Card(sb, "Total", ReportFormat.Duration(active + inactiveTime));
        Card(sb, "Active", ReportFormat.Duration(active));
        Card(sb, "Calls", ReportFormat.Duration(callTime));
        Card(sb, "Inactive", ReportFormat.Duration(inactiveTime));
        sb.Append("</div>\n");
    }

    /// <summary>
    /// Stretches of the day that ended up on no timesheet line: idle or locked time, and activity
    /// that matched no rule. Both are "time you'll have to account for from memory", which is why
    /// they share a tab — the Unclassified tab is for teaching Tally a rule, this one is for
    /// spotting the hole before someone asks about it.
    /// </summary>
    private static (List<string> Lines, TimeSpan Total) LostTime(
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<InactivePeriod> inactive,
        TimeSpan threshold)
    {
        var entries = inactive
            .Where(p => p.Duration >= threshold)
            .Select(g => (g.Start, g.Duration,
                Html: $"{ReportFormat.Clock(g.Start)}–{ReportFormat.Clock(g.End)} — {Esc(g.Reason)} <span class=\"muted\">({ReportFormat.Duration(g.Duration)})</span>"))
            .Concat(blocks
                .Where(b => b.Classification.IsUnclassified && b.Block.Duration >= threshold)
                .Select(b => (b.Block.Start, b.Block.Duration,
                    Html: $"{ReportFormat.Clock(b.Block.Start)}–{ReportFormat.Clock(b.Block.End)} — unclassified: “{Esc(b.Block.Title)}” <span class=\"muted\">({ReportFormat.Duration(b.Block.Duration)})</span>")))
            .OrderBy(x => x.Start)
            .ToList();

        return (
            entries.Select(x => x.Html).ToList(),
            TimeSpan.FromTicks(entries.Sum(x => x.Duration.Ticks)));
    }

    private static void AppendLostTime(StringBuilder sb, IReadOnlyList<string> lines, TimeSpan total)
    {
        if (lines.Count == 0)
        {
            sb.Append("<p class=\"empty\">Nothing unaccounted for — every stretch over five minutes is either classified or on a timesheet line.</p>\n");
            return;
        }

        sb.Append($"<p class=\"hint\">{ReportFormat.Duration(total)} across {lines.Count} {(lines.Count == 1 ? "stretch" : "stretches")} that no timesheet line covers.</p>\n");
        sb.Append("<div class=\"gaps\">\n<ul>\n");
        foreach (var line in lines)
            sb.Append("<li>").Append(line).Append("</li>\n");
        sb.Append("</ul>\n</div>\n");
    }

    // Window activity AND calls, merged into one time-ordered table so the Rollup is a complete
    // picture of the day. Calls carry the "Call" category badge; they overlay (don't replace) the
    // focused-window rows, so a call and its underlying window can both appear.
    private static void AppendRollup(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers, bool editable, IReadOnlyDictionary<string, string>? ticketOverrides)
    {
        var rows = RollupBuilder.Build(blocks)
            .Concat(RollupBuilder.BuildCalls(calls, ticketOverrides))
            .Concat(RollupBuilder.BuildTimers(timers))
            .Where(r => r.Time >= RollupBuilder.MinRollupDuration)   // hide sub-minute noise
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase);

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Category</th><th>Detail</th><th>Ticket</th><th class=\"num\">Time</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            sb.Append("<tr><td>").Append(CategoryBadge(row.Category)).Append("</td>")
              .Append("<td>").Append(Esc(ReportFormat.Detail(row.Client, row.DetailName))).Append("</td>")
              .Append("<td>").Append(TicketCell(row, editable)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(row.Time)).Append("</td></tr>\n");
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

    private static void AppendCalls(StringBuilder sb, IReadOnlyList<CallSpan> calls)
    {
        if (calls.Count == 0)
        {
            sb.Append("<p class=\"empty\">No calls recorded today.</p>\n");
            return;
        }

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Start</th><th>End</th><th class=\"num\">Duration</th><th>App</th><th>Title</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var call in calls)
        {
            sb.Append("<tr><td>").Append(ReportFormat.Clock(call.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(call.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(call.Duration)).Append("</td>")
              .Append("<td>").Append(Esc(call.ProcessName)).Append("</td>")
              .Append("<td>").Append(Esc(call.Title)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void AppendTimeline(StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks)
    {
        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Start</th><th>End</th><th class=\"num\">Duration</th><th>Category</th><th>Title</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        // Newest first — most recent activity at the top.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            sb.Append("<tr><td>").Append(ReportFormat.Clock(b.Block.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(b.Block.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(b.Block.Duration)).Append("</td>")
              .Append("<td>").Append(CategoryBadge(b.Classification.Category)).Append("</td>")
              .Append("<td>").Append(Esc(b.Block.Title)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void Card(StringBuilder sb, string label, string value)
        => sb.Append($"<div class=\"card\"><div class=\"v\">{value}</div><div class=\"l\">{label}</div></div>\n");

    private static string CategoryBadge(string category)
        => $"<span class=\"cat\" style=\"background:rgba({CategoryRgb(category)},.22)\">{Esc(category)}</span>";

    /// <summary>The category the shipped Teams chat rule files a focused conversation under.</summary>
    private const string TeamsChatCategory = "Teams - Chat";

    // The hue a category is drawn in, as bare RGB so callers can pick their own alpha — a pill wants
    // a wash, a calendar block's edge wants the full colour. Text always uses the theme foreground,
    // so contrast holds in both themes.
    private static string CategoryRgb(string category) => category switch
    {
        "HaloPSA" => "59,130,246",
        // Teams keeps one hue whether the time was a call or a chat — the badge text draws the
        // distinction, and the colour is there to say "this was Teams" at a glance.
        "Teams" or CallApps.TeamsCallCategory or TeamsChatCategory => "139,92,246",
        CallApps.DiscordCategory => "88,101,242",
        CallApps.RingCentralCategory => "6,182,212",
        "Email" => "20,184,166",
        "Development" => "34,197,94",
        "Browsing" => "234,179,8",
        "Remote Support" => "236,72,153",
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
        .uc-cat,.uc-scope { background:var(--bg); border:1px solid var(--border); border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:3px 6px; }
        .uc-cat { width:150px; }
        .uc-cat::placeholder { color:var(--muted); }
        .uc-cat:focus,.uc-scope:focus { outline:none; border-color:var(--accent); }
        .uc-save { font-size:13px; padding:5px 12px; white-space:nowrap; }
        .uc-save:disabled { background:var(--border); color:var(--muted); cursor:default; }
        .cal { position:relative; margin:14px 0 4px; }
        .cal-hr { position:absolute; left:0; right:0; border-top:1px solid var(--border); }
        .cal-hr.half { border-top-style:dotted; opacity:.55; }
        .cal-hr span { position:absolute; top:-8px; left:0; width:52px; text-align:right;
          padding-right:8px; background:var(--bg); color:var(--muted); font-size:11px; white-space:nowrap; }
        .cal-ev { position:absolute; left:60px; right:0; top:0; bottom:0; }
        .ev { position:absolute; box-sizing:border-box; overflow:hidden; border-radius:5px;
          border-left:3px solid; font-size:12px; line-height:17px; cursor:default; }
        .ev-fill { position:absolute; left:0; right:0; top:0; display:block; }
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

    // Swaps fresh <main> content in without a reload, keeping scroll steady and the selected tab.
    // Skips the swap whenever a field is focused (a ticket, a timer name, a triage category) so a
    // refresh never wipes an in-progress edit; the next tick lands once focus moves on.
    private const string LiveUpdateScript =
        "window.tallyUpdate=function(h){var a=document.activeElement;if(a&&(a.tagName==='INPUT'||a.tagName==='SELECT'))return;var y=window.scrollY;var m=document.getElementById('tally-live');if(m){m.innerHTML=h;if(window.tallyApplyActiveTab){window.tallyApplyActiveTab();}window.scrollTo(0,y);}};";

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
    private const string TimerControlScript =
        """
        (function(){
        function post(m){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}
        function name(){var n=document.querySelector('.tm-name');return n?n.value:'';}
        document.addEventListener('click',function(e){
        var b=e.target.closest?e.target.closest('.tm-go'):null;
        if(b){post({type:'timerToggle',value:name()});return;}
        var d=e.target.closest?e.target.closest('.tm-del'):null;
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

    // "Save rule" in the Unclassified tab: posts {type:'rule', process, title, scope, category} to the
    // host, which writes it into rules.json. The app + window travel as the base64 the row carries, so
    // nothing is re-derived from display text. An empty category focuses the field instead of posting.
    private const string RuleSaveScript =
        """
        (function(){
        document.addEventListener('click',function(e){
        var b=e.target.closest?e.target.closest('.uc-save'):null;if(!b||b.disabled)return;
        var r=b.closest('tr');if(!r)return;
        var c=r.querySelector('.uc-cat');var s=r.querySelector('.uc-scope');
        var v=c?c.value.trim():'';
        if(!v){if(c)c.focus();return;}
        if(!window.chrome||!window.chrome.webview)return;
        b.disabled=true;b.textContent='Saved';
        window.chrome.webview.postMessage({type:'rule',process:r.getAttribute('data-p'),title:r.getAttribute('data-t'),scope:s?s.value:'app',category:v});});
        })();
        """;

}
