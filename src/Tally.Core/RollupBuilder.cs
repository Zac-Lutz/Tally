using Tally.Core.Models;

namespace Tally.Core;

/// <summary>One aggregated rollup line: a category + specific activity with its summed time.</summary>
public sealed record RollupRow(
    string Category, string? Client, string? TicketRef, string DetailName,
    TimeSpan Time, int Keystrokes, int MouseClicks);

/// <summary>
/// Builds the report rollup at per-activity granularity. Each distinct activity gets its own row:
/// a Teams chat (by subject), a Halo ticket (by ticket number, so differing titles still merge),
/// or otherwise an individual window/browser tab (by normalized title). Rows are summed across
/// the whole day and ordered by time spent.
/// </summary>
public static class RollupBuilder
{
    public static IReadOnlyList<RollupRow> Build(IReadOnlyList<ClassifiedBlock> blocks)
        => blocks
            .GroupBy(b => (b.Classification.Category, b.Classification.Client, b.Classification.TicketRef, Key: ActivityKey(b)))
            .Select(g => new RollupRow(
                g.Key.Category,
                g.Key.Client,
                g.Key.TicketRef,
                DisplayName(g),
                TimeSpan.FromTicks(g.Sum(x => x.Block.Duration.Ticks)),
                g.Sum(x => x.Activity.Keystrokes),
                g.Sum(x => x.Activity.MouseClicks)))
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The within-(category, client, ticket) grouping key. Null for ticketed blocks so all views of
    // one ticket merge regardless of title; the subject for Teams chats; else the per-tab title.
    private static string? ActivityKey(ClassifiedBlock b)
        => b.Classification.Subject
           ?? (b.Classification.TicketRef is not null ? null : TitleNormalizer.Normalize(b.Block.Title));

    private static string DisplayName(IEnumerable<ClassifiedBlock> group)
    {
        var blocks = group.ToList();
        var subject = blocks.Select(b => b.Classification.Subject).FirstOrDefault(s => s is not null);
        if (subject is not null)
            return subject;

        // Represent the row by the normalized title of its longest-running block.
        var representative = blocks.OrderByDescending(b => b.Block.Duration).First().Block.Title;
        return TitleNormalizer.Normalize(representative);
    }
}
