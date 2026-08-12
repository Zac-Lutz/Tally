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
        TimeSpan? gapThreshold = null)
    {
        var threshold = gapThreshold ?? TimeSpan.FromMinutes(5);
        var sb = new StringBuilder();

        sb.AppendLine($"# Tally — {date:yyyy-MM-dd} ({date.DayOfWeek})");
        sb.AppendLine();

        if (blocks.Count == 0 && calls.Count == 0)
        {
            sb.AppendLine("No activity recorded.");
            return sb.ToString();
        }

        AppendSummary(sb, blocks, calls, inactivePeriods);
        AppendRollup(sb, blocks);
        AppendCalls(sb, calls);
        AppendTimeline(sb, blocks);
        AppendGaps(sb, blocks, inactivePeriods, threshold);

        return sb.ToString();
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
        var totalKeys = blocks.Sum(b => b.Activity.Keystrokes);
        var totalClicks = blocks.Sum(b => b.Activity.MouseClicks);
        var first = blocks.Count > 0 ? blocks[0].Block.Start : calls[0].Start;
        var last = blocks.Count > 0 ? blocks[^1].Block.End : calls[^1].End;

        sb.AppendLine(
            $"Tracked {Clock(first)}\u2013{Clock(last)} \u00b7 active {Fmt(active)} \u00b7 calls {Fmt(callTime)} \u00b7 inactive {Fmt(inactiveTime)} \u00b7 {totalKeys} keys \u00b7 {totalClicks} clicks");
        sb.AppendLine();
    }

    private static void AppendRollup(StringBuilder sb, IReadOnlyList<ClassifiedBlock> blocks)
    {
        sb.AppendLine("## Rollup");
        sb.AppendLine();
        sb.AppendLine("| Category | Detail | Ticket | Time | Keys/Clk |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var row in RollupBuilder.Build(blocks))
        {
            var ticket = row.TicketRef is { } t ? $"#{t}" : string.Empty;
            sb.AppendLine(
                $"| {Esc(row.Category)} | {Esc(Detail(row.Client, row.DetailName))} | {Esc(ticket)} | {Fmt(row.Time)} | {ActivityCell(row.Keystrokes, row.MouseClicks)} |");
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
        sb.AppendLine("| Start | End | Duration | Category | Keys/Clk | Title |");
        sb.AppendLine("|---|---|---|---|---|---|");
        // Newest first — most recent activity at the top.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            sb.AppendLine(
                $"| {Clock(b.Block.Start)} | {Clock(b.Block.End)} | {Fmt(b.Block.Duration)} | {Esc(b.Classification.Category)} | {ActivityCell(b.Activity.Keystrokes, b.Activity.MouseClicks)} | {Esc(b.Block.Title)} |");
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
                Line: $"- {Clock(b.Block.Start)}\u2013{Clock(b.Block.End)} \u2014 unclassified: \"{Esc(b.Block.Title)}\" ({Fmt(b.Block.Duration)})")))
            .OrderBy(x => x.Start);

        foreach (var (_, line) in lines)
            sb.AppendLine(line);

        sb.AppendLine();
    }

    private static string Detail(string? client, string? subject) => ReportFormat.Detail(client, subject);

    private static string ActivityCell(int keys, int clicks)
        => keys == 0 && clicks == 0 ? "—" : $"{keys}/{clicks}";

    private static string Clock(DateTimeOffset t) => ReportFormat.Clock(t);

    private static string Fmt(TimeSpan t) => ReportFormat.Duration(t);

    private static string Esc(string s)
        => s.Replace("|", "\\|").Replace("\r", string.Empty).Replace("\n", " ");
}
