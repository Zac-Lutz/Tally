using System.Text;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>Renders a day's sessions as a self-contained, theme-aware HTML page for time entry.</summary>
public static class HtmlReportWriter
{
    public static string BuildHtml(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        string? embeddedJson = null,
        TimeSpan? gapThreshold = null,
        IReadOnlyList<ManualTimer>? timers = null,
        IReadOnlyDictionary<string, string>? ticketOverrides = null)
    {
        var threshold = gapThreshold ?? TimeSpan.FromMinutes(5);
        var timerList = timers ?? [];
        var showExport = embeddedJson is not null && (blocks.Count > 0 || calls.Count > 0 || timerList.Count > 0);
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Tally — {ReportFormat.DisplayDate(date)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timerList, threshold, showExport,
            includeHeader: true, editable: false, ticketOverrides: ticketOverrides);

        sb.Append("</main>\n");
        sb.Append("<script>").Append(TabScript).Append("</script>\n");
        if (showExport)
        {
            // The JSON is already default-encoder-escaped (< > & as \u00xx), so it can't break out
            // of the script element. The download is built client-side from this embedded copy.
            sb.Append("<script type=\"application/json\" id=\"tally-export\">").Append(embeddedJson).Append("</script>\n");
            sb.Append("<script>").Append(ExportScript).Append("</script>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
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
        IReadOnlyDictionary<string, string>? ticketOverrides = null)
    {
        var sb = new StringBuilder();
        AppendMainInner(sb, date, blocks, calls, inactivePeriods, timers ?? [],
            gapThreshold ?? TimeSpan.FromMinutes(5), showExport: false, includeHeader: false, editable: true,
            ticketOverrides: ticketOverrides);
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
        bool showExport,
        bool includeHeader,
        bool editable,
        IReadOnlyDictionary<string, string>? ticketOverrides)
    {
        if (includeHeader)
        {
            sb.Append("<div class=\"head\">\n");
            sb.Append($"<h1>Tally <span class=\"date\">{ReportFormat.DisplayDate(date)} · {date.DayOfWeek}</span></h1>\n");
            if (showExport)
                sb.Append($"<button id=\"export-json\" type=\"button\" data-filename=\"tally-{date:yyyy-MM-dd}.json\">Export JSON</button>\n");
            sb.Append("</div>\n");
        }

        if (blocks.Count == 0 && calls.Count == 0 && timers.Count == 0)
        {
            sb.Append("<p class=\"empty\">No activity recorded.</p>\n");
            return;
        }

        AppendSummary(sb, blocks, calls, inactivePeriods);
        AppendGaps(sb, blocks, inactivePeriods, threshold);
        AppendTabs(sb, blocks, calls, timers, editable, ticketOverrides);
    }

    // Rollup / Calls / Timeline / Timers as switchable tabs (Rollup active by default) instead of
    // stacked sections. Tab switching + preserving the choice across live refreshes is TabScript.
    private static void AppendTabs(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers, bool editable, IReadOnlyDictionary<string, string>? ticketOverrides)
    {
        sb.Append("<div class=\"tabs\">");
        sb.Append("<button class=\"tab active\" type=\"button\" data-tab=\"rollup\">Rollup</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"calls\">Calls</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timeline\">Timeline</button>");
        sb.Append("<button class=\"tab\" type=\"button\" data-tab=\"timers\">Timers</button>");
        sb.Append("</div>\n");

        sb.Append("<section class=\"panel active\" data-panel=\"rollup\">\n");
        AppendRollup(sb, blocks, calls, timers, editable, ticketOverrides);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"calls\">\n");
        AppendCalls(sb, calls);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timeline\">\n");
        AppendTimeline(sb, blocks);
        sb.Append("</section>\n");
        sb.Append("<section class=\"panel\" data-panel=\"timers\">\n");
        AppendTimers(sb, timers, editable);
        sb.Append("</section>\n");
    }

    // The Timers tab. In the live view the name is editable (renaming a recorded timer); the change
    // persists and reflects on the Rollup. The saved file report shows names read-only.
    private static void AppendTimers(StringBuilder sb, IReadOnlyList<ManualTimer> timers, bool editable)
    {
        if (timers.Count == 0)
        {
            sb.Append("<p class=\"empty\">No timers recorded today.</p>\n");
            return;
        }

        sb.Append("<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Timer</th><th>Start</th><th>End</th><th class=\"num\">Duration</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var t in timers.OrderByDescending(t => t.Start))
        {
            var nameCell = editable
                ? $"<input class=\"tn\" type=\"text\" data-timer-id=\"{t.Id}\" value=\"{Esc(t.Name)}\" aria-label=\"Timer name\">"
                : Esc(t.Name);
            sb.Append("<tr><td>").Append(nameCell).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(t.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(t.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(t.Duration)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
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

    private static void AppendGaps(
        StringBuilder sb,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<InactivePeriod> inactive,
        TimeSpan threshold)
    {
        var idleGaps = inactive.Where(p => p.Duration >= threshold).ToList();
        var unclassified = blocks
            .Where(b => b.Classification.IsUnclassified && b.Block.Duration >= threshold)
            .ToList();

        if (idleGaps.Count == 0 && unclassified.Count == 0)
            return;

        var lines = idleGaps
            .Select(g => (g.Start,
                Html: $"{ReportFormat.Clock(g.Start)}–{ReportFormat.Clock(g.End)} — {Esc(g.Reason)} <span class=\"muted\">({ReportFormat.Duration(g.Duration)})</span>"))
            .Concat(unclassified.Select(b => (b.Block.Start,
                Html: $"{ReportFormat.Clock(b.Block.Start)}–{ReportFormat.Clock(b.Block.End)} — unclassified: “{Esc(b.Block.Title)}” <span class=\"muted\">({ReportFormat.Duration(b.Block.Duration)})</span>")))
            .OrderBy(x => x.Start);

        sb.Append("<h2>Gaps to account for</h2>\n<div class=\"gaps\">\n<ul>\n");
        foreach (var (_, html) in lines)
            sb.Append("<li>").Append(html).Append("</li>\n");
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
            var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(rowKey));
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
        => $"<span class=\"cat\" style=\"background:{CategoryColor(category)}\">{Esc(category)}</span>";

    // Translucent hue only — the pill text uses the theme foreground, so contrast holds in both themes.
    private static string CategoryColor(string category) => category switch
    {
        "HaloPSA" => "rgba(59,130,246,.20)",
        "Teams" => "rgba(139,92,246,.20)",
        "Email" => "rgba(20,184,166,.20)",
        "Development" => "rgba(34,197,94,.20)",
        "Browsing" => "rgba(234,179,8,.22)",
        "Remote Support" => "rgba(236,72,153,.20)",
        "Call" => "rgba(249,115,22,.22)",
        "Timer" => "rgba(99,102,241,.24)",
        _ => "rgba(148,163,184,.22)",
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
          --accent:#0d8a78; --btn-fg:#fff;
          --accent-weak:rgba(18,168,145,.12); --warn-bg:rgba(214,158,46,.12); --warn-border:rgba(214,158,46,.5);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg:#16181c; --card:#1e2126; --fg:#e6e9ec; --muted:#9aa4ae; --border:#2c3138;
            --accent:#2fd4b6; --btn-fg:#08201c;
            --accent-weak:rgba(47,212,182,.14); --warn-bg:rgba(214,158,46,.16); --warn-border:rgba(214,158,46,.55);
          }
        }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--fg);
          font:15px/1.5 -apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
        main { max-width:1080px; margin:0 auto; padding:32px 24px 64px; }
        .head { display:flex; justify-content:space-between; align-items:center; gap:16px; flex-wrap:wrap; }
        button#export-json { background:var(--accent); color:var(--btn-fg); border:none; border-radius:8px;
          padding:8px 14px; font:inherit; font-weight:600; cursor:pointer; }
        button#export-json:hover { filter:brightness(1.06); }
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
        .tk,.tn { background:transparent; border:1px solid transparent; border-radius:6px;
          color:var(--fg); font:inherit; font-size:13px; padding:2px 6px; }
        .tk { width:84px; }
        .tn { width:280px; max-width:100%; }
        .tk::placeholder { color:var(--muted); }
        .tk:hover,.tn:hover { border-color:var(--border); }
        .tk:focus,.tn:focus { outline:none; border-color:var(--accent); background:var(--bg); }
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
        document.addEventListener('click',function(e){var t=e.target.closest?e.target.closest('.tab'):null;if(!t)return;window.__tallyTab=t.getAttribute('data-tab');apply();});
        document.addEventListener('DOMContentLoaded',apply);
        })();
        """;

    // Swaps fresh <main> content in without a reload, keeping scroll steady and the selected tab.
    // Skips the swap while a ticket cell OR timer-name cell is being edited so a refresh never wipes
    // an in-progress edit.
    private const string LiveUpdateScript =
        "window.tallyUpdate=function(h){var a=document.activeElement;if(a&&a.classList&&(a.classList.contains('tk')||a.classList.contains('tn')))return;var y=window.scrollY;var m=document.getElementById('tally-live');if(m){m.innerHTML=h;if(window.tallyApplyActiveTab){window.tallyApplyActiveTab();}window.scrollTo(0,y);}};";

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

    // Builds the .json download client-side from the embedded copy — works offline, no server.
    private const string ExportScript =
        """
        (function(){var b=document.getElementById('export-json');if(!b)return;
        b.addEventListener('click',function(){
        var t=document.getElementById('tally-export').textContent;
        var blob=new Blob([t],{type:'application/json'});
        var u=URL.createObjectURL(blob);
        var a=document.createElement('a');
        a.href=u;a.download=b.getAttribute('data-filename')||'tally.json';
        document.body.appendChild(a);a.click();document.body.removeChild(a);
        URL.revokeObjectURL(u);});})();
        """;
}
