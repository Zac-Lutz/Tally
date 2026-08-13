using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// One activity that matched no rule, awaiting triage: the app, the window it ran in, and how long
/// it ran today. <see cref="Title"/> is the normalized title — the same text the Rollup shows — so a
/// rule drafted from a row matches what the user actually pointed at.
/// </summary>
public sealed record UnclassifiedRow(string ProcessName, string Title, TimeSpan Time);

/// <summary>
/// Groups the day's unclassified blocks into triage rows: one row per (app, window title), summed
/// across the day and ordered by time spent. The same activity returned to five times is one row to
/// give a rule, not five.
/// </summary>
public static class UnclassifiedBuilder
{
    public static IReadOnlyList<UnclassifiedRow> Build(
        IReadOnlyList<ClassifiedBlock> blocks, TimeSpan? minDuration = null)
    {
        // Same sub-minute noise floor as the Rollup: a two-second window isn't worth a rule.
        var min = minDuration ?? RollupBuilder.MinRollupDuration;
        return blocks
            .Where(b => b.Classification.IsUnclassified)
            .GroupBy(b => (b.Block.ProcessName, Title: TitleNormalizer.Normalize(b.Block.Title)))
            .Select(g => new UnclassifiedRow(
                g.Key.ProcessName,
                g.Key.Title,
                TimeSpan.FromTicks(g.Sum(b => b.Block.Duration.Ticks))))
            .Where(r => r.Time >= min)
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
