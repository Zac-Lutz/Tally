using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// One ticket's day: every block that named it, summed — however many apps and windows the work
/// crossed. <see cref="Visits"/> counts distinct sittings, merged the same way the Timesheet
/// calendar merges its pins, so the two figures agree.
/// </summary>
public sealed record TicketRow(
    string TicketRef,
    string Category,
    string ProcessName,
    string Detail,
    int Visits,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    TimeSpan Time);

/// <summary>
/// Groups the day by ticket number for the Tickets tab. A block belongs to a ticket when its
/// window title carried the number (a rule's <c>(?&lt;ticket&gt;)</c> capture) or the user typed
/// one onto its Rollup row — the same effective ticket every other view bills by.
/// </summary>
public static class TicketsBuilder
{
    public static IReadOnlyList<TicketRow> Build(IReadOnlyList<ClassifiedBlock> blocks)
        => blocks
            .Where(b => b.EffectiveTicket is not null && b.Block.Duration > TimeSpan.Zero)
            .GroupBy(b => b.EffectiveTicket!)
            .Select(g =>
            {
                var ordered = g.OrderBy(b => b.Block.Start).ToList();
                return new TicketRow(
                    g.Key,
                    Dominant(ordered, b => b.Classification.Category),
                    Dominant(ordered, b => b.Block.ProcessName),
                    Dominant(ordered, b => TitleNormalizer.Normalize(b.Block.Title)),
                    CountVisits(ordered),
                    ordered[0].Block.Start,
                    ordered.Max(b => b.Block.End),
                    TimeSpan.FromTicks(ordered.Sum(b => b.Block.Duration.Ticks)));
            })
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.TicketRef, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Distinct sittings: time-ordered blocks merged across sub-minute gaps — the same rule the
    // Timesheet calendar pins by, so "3 visits" here matches three pins there.
    private static int CountVisits(IReadOnlyList<ClassifiedBlock> ordered)
    {
        var visits = 0;
        var lastEnd = DateTimeOffset.MinValue;
        foreach (var block in ordered)
        {
            if (block.Block.Start - lastEnd > TimesheetCalendar.VisitMergeGap)
                visits++;
            if (block.Block.End > lastEnd)
                lastEnd = block.Block.End;
        }

        return visits;
    }

    // The value the most time went to — the honest representative of a mixed group.
    private static string Dominant(IReadOnlyList<ClassifiedBlock> blocks, Func<ClassifiedBlock, string> select)
        => blocks
            .Select(b => (Value: select(b), b.Block.Duration))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Value)
            .OrderByDescending(g => g.Sum(x => x.Duration.Ticks))
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;
}
