using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// One ticket's day: everything that named it, summed — however many apps, windows, and calls the
/// work crossed. <see cref="Visits"/> counts distinct sittings, merged the same way the Timesheet
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
/// one onto its Rollup row; a call belongs when its Rollup row was given one. The same effective
/// ticket every other view bills by — the rollup, the export, and this tab tell one story.
/// </summary>
public static class TicketsBuilder
{
    // One stretch of a ticket's day, whatever kind of evidence it came from.
    private readonly record struct Contribution(
        DateTimeOffset Start, DateTimeOffset End, string Process, string Category, string Detail, string Ticket)
    {
        public TimeSpan Duration => End - Start;
    }

    public static IReadOnlyList<TicketRow> Build(
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan>? calls = null,
        IReadOnlyDictionary<string, string>? ticketOverrides = null)
    {
        // Excluded activity is not work to bill, so it contributes nothing here even on the rare
        // occasion its title carries a ticket number.
        var contributions = blocks
            .Where(b => !b.Classification.Excluded
                        && b.EffectiveTicket is not null && b.Block.Duration > TimeSpan.Zero)
            .Select(b => new Contribution(
                b.Block.Start, b.Block.End, b.Block.ProcessName, b.Classification.Category,
                TitleNormalizer.Normalize(b.Block.Title), b.EffectiveTicket!))
            .ToList();

        // A ticket typed on a call's rollup row files the call here too.
        foreach (var call in (calls ?? []).Where(c => c.End > c.Start))
        {
            if (ticketOverrides?.GetValueOrDefault(RollupBuilder.CallOverrideKey(call)) is not { } ticket)
                continue;

            var title = TitleNormalizer.Normalize(call.Title);
            contributions.Add(new Contribution(
                call.Start, call.End, call.ProcessName, CallApps.CategoryFor(call.ProcessName),
                title.Length > 0 ? title : call.ProcessName, ticket));
        }

        return contributions
            .GroupBy(c => c.Ticket)
            .Select(g =>
            {
                var ordered = g.OrderBy(c => c.Start).ToList();
                return new TicketRow(
                    g.Key,
                    Dominant(ordered, c => c.Category),
                    Dominant(ordered, c => c.Process),
                    Dominant(ordered, c => c.Detail),
                    CountVisits(ordered),
                    ordered[0].Start,
                    ordered.Max(c => c.End),
                    UnionTime(ordered));
            })
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.TicketRef, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Distinct sittings: time-ordered contributions merged across sub-minute gaps — the same rule
    // the Timesheet calendar pins by, so "3 visits" here matches three pins there.
    private static int CountVisits(IReadOnlyList<Contribution> ordered)
    {
        var visits = 0;
        var lastEnd = DateTimeOffset.MinValue;
        foreach (var contribution in ordered)
        {
            if (contribution.Start - lastEnd > TimesheetCalendar.VisitMergeGap)
                visits++;
            if (contribution.End > lastEnd)
                lastEnd = contribution.End;
        }

        return visits;
    }

    // Wall-clock the ticket actually occupied. Contributions can overlap — a call assigned to the
    // ticket runs OVER the ticket's own windows — and the same minute must not count twice.
    // Blocks alone never overlap, so for them this equals the plain sum.
    private static TimeSpan UnionTime(IReadOnlyList<Contribution> ordered)
    {
        var total = TimeSpan.Zero;
        var (currentStart, currentEnd) = (ordered[0].Start, ordered[0].End);
        foreach (var contribution in ordered.Skip(1))
        {
            if (contribution.Start <= currentEnd)
            {
                if (contribution.End > currentEnd)
                    currentEnd = contribution.End;
            }
            else
            {
                total += currentEnd - currentStart;
                (currentStart, currentEnd) = (contribution.Start, contribution.End);
            }
        }

        return total + (currentEnd - currentStart);
    }

    // The value the most time went to — the honest representative of a mixed group.
    private static string Dominant(IReadOnlyList<Contribution> contributions, Func<Contribution, string> select)
        => contributions
            .Select(c => (Value: select(c), c.Duration))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Value)
            .OrderByDescending(g => g.Sum(x => x.Duration.Ticks))
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;
}
