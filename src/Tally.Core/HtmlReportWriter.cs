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
        TimeSpan? gapThreshold = null)
    {
        var threshold = gapThreshold ?? TimeSpan.FromMinutes(5);
        var showExport = embeddedJson is not null && (blocks.Count > 0 || calls.Count > 0);
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Tally — {date:yyyy-MM-dd}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

        AppendMainInner(sb, date, blocks, calls, inactivePeriods, threshold, showExport);

        sb.Append("</main>\n");
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
    /// The content that goes INSIDE &lt;main&gt; — the same sections BuildHtml renders, without the
    /// page shell or export button. The live view swaps this into its window each refresh, so the
    /// live dashboard and the file report always show identical information.
    /// </summary>
    public static string BuildMainInner(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan? gapThreshold = null)
    {
        var sb = new StringBuilder();
        AppendMainInner(sb, date, blocks, calls, inactivePeriods, gapThreshold ?? TimeSpan.FromMinutes(5), showExport: false);
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
        sb.Append("<script>").Append(LiveUpdateScript).Append("</script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendMainInner(
        StringBuilder sb,
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan threshold,
        bool showExport)
    {
        sb.Append("<div class=\"head\">\n");
        sb.Append($"<h1>Tally <span class=\"date\">{date:yyyy-MM-dd} · {date.DayOfWeek}</span></h1>\n");
        if (showExport)
            sb.Append($"<button id=\"export-json\" type=\"button\" data-filename=\"tally-{date:yyyy-MM-dd}.json\">Export JSON</button>\n");
        sb.Append("</div>\n");

        if (blocks.Count == 0 && calls.Count == 0)
        {
            sb.Append("<p class=\"empty\">No activity recorded.</p>\n");
            return;
        }

        AppendSummary(sb, blocks, calls, inactivePeriods);
        AppendGaps(sb, blocks, inactivePeriods, threshold);
        AppendRollup(sb, blocks);
        AppendCalls(sb, calls);
        AppendTimeline(sb, blocks);
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
        var keys = blocks.Sum(b => b.Activity.Keystrokes);
        var clicks = blocks.Sum(b => b.Activity.MouseClicks);
        var first = blocks.Count > 0 ? blocks[0].Block.Start : calls[0].Start;
        var last = blocks.Count > 0 ? blocks[^1].Block.End : calls[^1].End;

        sb.Append($"<p class=\"tracked\">Tracked {ReportFormat.Clock(first)}–{ReportFormat.Clock(last)}</p>\n");
        sb.Append("<div class=\"cards\">\n");
        Card(sb, "Active", ReportFormat.Duration(active));
        Card(sb, "Calls", ReportFormat.Duration(callTime));
        Card(sb, "Inactive", ReportFormat.Duration(inactiveTime));
        Card(sb, "Keys", keys.ToString("N0"));
        Card(sb, "Clicks", clicks.ToString("N0"));
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

    private static void AppendRollup(StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks)
    {
        sb.Append("<h2>Rollup</h2>\n<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Category</th><th>Detail</th><th>Ticket</th><th class=\"num\">Time</th><th class=\"num\">Keys/Clk</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        foreach (var row in RollupBuilder.Build(blocks))
        {
            var ticket = row.TicketRef is { } t ? $"#{Esc(t)}" : string.Empty;
            sb.Append("<tr><td>").Append(CategoryBadge(row.Category)).Append("</td>")
              .Append("<td>").Append(Esc(ReportFormat.Detail(row.Client, row.DetailName))).Append("</td>")
              .Append("<td>").Append(ticket).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(row.Time)).Append("</td>")
              .Append("<td class=\"num\">").Append(Activity(row.Keystrokes, row.MouseClicks)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void AppendCalls(StringBuilder sb, IReadOnlyList<CallSpan> calls)
    {
        if (calls.Count == 0)
            return;

        sb.Append("<h2>Calls</h2>\n<div class=\"scroll\">\n<table>\n<thead>\n");
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
        sb.Append("<h2>Timeline</h2>\n<div class=\"scroll\">\n<table>\n<thead>\n");
        sb.Append("<tr><th>Start</th><th>End</th><th class=\"num\">Duration</th><th>Category</th><th class=\"num\">Keys/Clk</th><th>Title</th></tr>\n");
        sb.Append("</thead>\n<tbody>\n");
        // Newest first — most recent activity at the top.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            sb.Append("<tr><td>").Append(ReportFormat.Clock(b.Block.Start)).Append("</td>")
              .Append("<td>").Append(ReportFormat.Clock(b.Block.End)).Append("</td>")
              .Append("<td class=\"num\">").Append(ReportFormat.Duration(b.Block.Duration)).Append("</td>")
              .Append("<td>").Append(CategoryBadge(b.Classification.Category)).Append("</td>")
              .Append("<td class=\"num\">").Append(Activity(b.Activity.Keystrokes, b.Activity.MouseClicks)).Append("</td>")
              .Append("<td>").Append(Esc(b.Block.Title)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void Card(StringBuilder sb, string label, string value)
        => sb.Append($"<div class=\"card\"><div class=\"v\">{value}</div><div class=\"l\">{label}</div></div>\n");

    private static string CategoryBadge(string category)
        => $"<span class=\"cat\" style=\"background:{CategoryColor(category)}\">{Esc(category)}</span>";

    private static string Activity(int keys, int clicks)
        => keys == 0 && clicks == 0 ? "<span class=\"muted\">—</span>" : $"{keys}/{clicks}";

    // Translucent hue only — the pill text uses the theme foreground, so contrast holds in both themes.
    private static string CategoryColor(string category) => category switch
    {
        "HaloPSA" => "rgba(59,130,246,.20)",
        "Teams" => "rgba(139,92,246,.20)",
        "Email" => "rgba(20,184,166,.20)",
        "Development" => "rgba(34,197,94,.20)",
        "Browsing" => "rgba(234,179,8,.22)",
        "Remote Support" => "rgba(236,72,153,.20)",
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
        """;

    // Swaps fresh <main> content in without a reload, keeping the scroll position steady.
    private const string LiveUpdateScript =
        "window.tallyUpdate=function(h){var y=window.scrollY;var m=document.getElementById('tally-live');if(m){m.innerHTML=h;window.scrollTo(0,y);}};";

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
