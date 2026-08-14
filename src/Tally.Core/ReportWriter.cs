using System.Text;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>Renders a day's sessions as a markdown report for time entry.</summary>
public static class ReportWriter
{
    public static string BuildMarkdown(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan? gapThreshold = null,
        IReadOnlyList<ManualTimer>? timers = null,
        IReadOnlyDictionary<string, string>? ticketOverrides = null)
    {
        var threshold = gapThreshold ?? TimeSpan.FromMinutes(5);
        var timerList = timers ?? [];
        var sb = new StringBuilder();

        sb.AppendLine($"# Tally — {ReportFormat.DisplayDate(date)} ({date.DayOfWeek})");
        sb.AppendLine();

        if (blocks.Count == 0 && calls.Count == 0 && timerList.Count == 0)
        {
            sb.AppendLine("No activity recorded.");
            return sb.ToString();
        }

        AppendSummary(sb, blocks, calls, inactivePeriods);
        AppendRollup(sb, blocks, calls, timerList, ticketOverrides);
        AppendCalls(sb, calls);
        AppendTimeline(sb, blocks);
        AppendTimers(sb, timerList);
        AppendGaps(sb, blocks, inactivePeriods, threshold);

        return sb.ToString();
    }

    private static void AppendTimers(StringBuilder sb, IReadOnlyList<ManualTimer> timers)
    {
        if (timers.Count == 0)
            return;

        sb.AppendLine("## Timers");
        sb.AppendLine();
        sb.AppendLine("| Timer | Start | End | Duration |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var t in timers.OrderByDescending(t => t.Start))
            sb.AppendLine($"| {Esc(t.Name)} | {Clock(t.Start)} | {Clock(t.End)} | {Fmt(t.Duration)} |");
        sb.AppendLine();
    }

    private static void AppendSummary(
        StringBuilder sb,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<InactivePeriod> inactivePeriods)
    {
        var active = TimeSpan.FromTicks(blocks.Sum(b => b.Block.Duration.Ticks));
        var callTime = TimeSpan.FromTicks(calls.Sum(c => c.Duration.Ticks));
        var inactiveTime = TimeSpan.FromTicks(inactivePeriods.Sum(p => p.Duration.Ticks));
        if (blocks.Count == 0 && calls.Count == 0)
            return;   // timers-only day: no auto-tracked range to summarize
        var first = blocks.Count > 0 ? blocks[0].Block.Start : calls[0].Start;
        var last = blocks.Count > 0 ? blocks[^1].Block.End : calls[^1].End;

        sb.AppendLine(
            $"Tracked {Clock(first)}\u2013{Clock(last)} \u00b7 total {Fmt(active + inactiveTime)} \u00b7 active {Fmt(active)} \u00b7 calls {Fmt(callTime)} \u00b7 inactive {Fmt(inactiveTime)}");
        sb.AppendLine();
    }

    // Window activity, calls, AND manual timers, merged into one time-ordered rollup (calls carry
    // the "Call" category, timers the "Timer" category), matching the HTML report's Rollup tab.
    private static void AppendRollup(
        StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers, IReadOnlyDictionary<string, string>? ticketOverrides)
    {
        var rows = RollupBuilder.Build(blocks)
            .Concat(RollupBuilder.BuildCalls(calls, ticketOverrides))
            .Concat(RollupBuilder.BuildTimers(timers))
            .Where(r => r.Time >= RollupBuilder.MinRollupDuration)   // hide sub-minute noise
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("## Rollup");
        sb.AppendLine();
        sb.AppendLine("| Category | Detail | Ticket | Time |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var row in rows)
        {
            var ticket = row.TicketRef is { } t ? $"#{t}" : string.Empty;
            sb.AppendLine(
                $"| {Esc(row.Category)} | {Esc(Detail(row.Client, row.DetailName))} | {Esc(ticket)} | {Fmt(row.Time)} |");
        }

        sb.AppendLine();
    }

    private static void AppendCalls(StringBuilder sb, IReadOnlyList<CallSpan> calls)
    {
        if (calls.Count == 0)
            return;

        sb.AppendLine("## Calls");
        sb.AppendLine();
        sb.AppendLine("| Start | End | Duration | App | Title |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var call in calls)
        {
            sb.AppendLine(
                $"| {Clock(call.Start)} | {Clock(call.End)} | {Fmt(call.Duration)} | {Esc(call.ProcessName)} | {Esc(call.Title)} |");
        }

        sb.AppendLine();
    }

    private static void AppendTimeline(StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks)
    {
        sb.AppendLine("## Timeline");
        sb.AppendLine();
        sb.AppendLine("| Start | End | Duration | Category | Title |");
        sb.AppendLine("|---|---|---|---|---|");
        // Newest first — most recent activity at the top.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            sb.AppendLine(
                $"| {Clock(b.Block.Start)} | {Clock(b.Block.End)} | {Fmt(b.Block.Duration)} | {Esc(b.Classification.Category)} | {Esc(b.Block.Title)} |");
        }

        sb.AppendLine();
    }

    private static void AppendGaps(
        StringBuilder sb,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<InactivePeriod> inactivePeriods,
        TimeSpan threshold)
    {
        var idleGaps = inactivePeriods.Where(p => p.Duration >= threshold).ToList();
        var unclassified = blocks
            .Where(b => b.Classification.IsUnclassified && b.Block.Duration >= threshold)
            .ToList();

        if (idleGaps.Count == 0 && unclassified.Count == 0)
            return;

        sb.AppendLine("## Gaps to account for");
        sb.AppendLine();

        var lines = idleGaps
            .Select(g => (g.Start, Line: $"- {Clock(g.Start)}\u2013{Clock(g.End)} \u2014 {g.Reason} ({Fmt(g.Duration)})"))
            .Concat(unclassified.Select(b => (b.Block.Start,
                Line: $"- {Clock(b.Block.Start)}\u2013{Clock(b.Block.End)} \u2014 uncategorized: \"{Esc(b.Block.Title)}\" ({Fmt(b.Block.Duration)})")))
            .OrderBy(x => x.Start);

        foreach (var (_, line) in lines)
            sb.AppendLine(line);

        sb.AppendLine();
    }

    private static string Detail(string? client, string? subject) => ReportFormat.Detail(client, subject);

    private static string Clock(DateTimeOffset t) => ReportFormat.Clock(t);

    private static string Fmt(TimeSpan t) => ReportFormat.Duration(t);

    private static string Esc(string s)
        => s.Replace("|", "\\|").Replace("\r", string.Empty).Replace("\n", " ");
}
