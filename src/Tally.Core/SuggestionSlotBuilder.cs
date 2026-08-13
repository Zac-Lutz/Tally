using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// One proposed timesheet entry. <see cref="Measured"/> is the time actually observed;
/// <see cref="Reported"/> is what goes on the timesheet after rounding. They're kept apart so a
/// verification screen can show both and the rounding never hides from the person reviewing it.
/// </summary>
public sealed record SuggestionSlot(
    string Category,
    string? TicketRef,
    string Label,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Measured,
    TimeSpan Reported,
    IReadOnlyList<ClassifiedBlock> Blocks,
    bool IsOddsAndEnds = false);

public sealed record SuggestionSlotOptions
{
    /// <summary>A new slot starts when an activity hasn't been touched for this long.</summary>
    public TimeSpan SessionGap { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Shorter than this and a slot isn't worth its own timesheet line — it gets rescued
    /// (see <see cref="SuggestionSlotBuilder"/>) rather than dropped.</summary>
    public TimeSpan MinimumSlot { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Reported time is rounded to the nearest multiple of this, never to zero.</summary>
    public TimeSpan Rounding { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Below this the odds-and-ends slot isn't worth emitting at all.</summary>
    public TimeSpan MinimumOddsAndEnds { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Turns a classified day into the handful of slots a timesheet actually wants.
/// <para>
/// The unit is the <b>billing target</b> — a ticket if one was detected, otherwise the category —
/// split into sessions, so a ticket worked on morning and afternoon is two entries sitting where
/// the work happened rather than one card smeared across the day.
/// </para>
/// <para>
/// Nothing is thrown away for being short, because losing ten two-minute visits loses twenty real
/// minutes. Anything under the minimum goes through two rescues: first the day's leftovers are
/// re-pooled per target (six two-minute visits to one ticket become one twelve-minute entry), and
/// whatever is still too small to stand alone is combined into a single "odds and ends" slot that
/// carries all of its detail. Only that slot is unattributable, and it's visible rather than
/// silently missing.
/// </para>
/// </summary>
public static class SuggestionSlotBuilder
{
    /// <summary>The category the combined leftovers slot carries.</summary>
    public const string OddsAndEndsCategory = "Odds and ends";

    public static IReadOnlyList<SuggestionSlot> Build(
        IReadOnlyList<ClassifiedBlock> blocks, SuggestionSlotOptions? options = null)
    {
        var opts = options ?? new SuggestionSlotOptions();

        var sessions = blocks
            .Where(b => b.Block.Duration > TimeSpan.Zero)
            .GroupBy(TargetKey)
            .SelectMany(target => Sessions(target.OrderBy(b => b.Block.Start).ToList(), opts.SessionGap))
            .ToList();

        var slots = new List<SuggestionSlot>();
        var leftovers = new List<List<ClassifiedBlock>>();
        foreach (var session in sessions)
        {
            if (Measured(session) >= opts.MinimumSlot)
                slots.Add(ToSlot(session, opts, SlotShape.Session));
            else
                leftovers.Add(session);
        }

        // Rescue 1 — the same target, revisited all day in snatches, is one entry's worth of work.
        var scattered = new List<ClassifiedBlock>();
        foreach (var target in leftovers.SelectMany(s => s).GroupBy(TargetKey))
        {
            var pooled = target.OrderBy(b => b.Block.Start).ToList();
            if (Measured(pooled) >= opts.MinimumSlot)
                slots.Add(ToSlot(pooled, opts, SlotShape.Pooled));
            else
                scattered.AddRange(pooled);
        }

        // Rescue 2 — everything still too small to name, kept as one visible line rather than lost.
        if (scattered.Count > 0 && Measured(scattered) >= opts.MinimumOddsAndEnds)
            slots.Add(ToSlot(scattered.OrderBy(b => b.Block.Start).ToList(), opts, SlotShape.OddsAndEnds));

        return slots.OrderBy(s => s.Start).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rounds to the nearest multiple, but never to nothing: any observed time reports at least one
    /// multiple. Rounding down to zero would delete work — and a zero-hour slot is rejected on
    /// import anyway.
    /// </summary>
    public static TimeSpan Round(TimeSpan measured, TimeSpan to)
    {
        if (to <= TimeSpan.Zero)
            return measured;

        var multiples = Math.Round(measured.Ticks / (double)to.Ticks, MidpointRounding.AwayFromZero);
        return to * Math.Max(1, multiples);
    }

    // The billing target: a ticket stands on its own regardless of which app it was worked in
    // (a Halo tab, a remote session and an email about #4867 are all #4867). Everything else
    // groups by category, which is what a non-ticketed line gets booked against.
    private static string TargetKey(ClassifiedBlock b)
        => b.EffectiveTicket is { } ticket ? $"T␟{ticket}" : $"C␟{b.Classification.Category}";

    private static IEnumerable<List<ClassifiedBlock>> Sessions(List<ClassifiedBlock> ordered, TimeSpan gap)
    {
        var current = new List<ClassifiedBlock>();
        foreach (var block in ordered)
        {
            if (current.Count > 0 && block.Block.Start - current[^1].Block.End > gap)
            {
                yield return current;
                current = [];
            }

            current.Add(block);
        }

        if (current.Count > 0)
            yield return current;
    }

    private static TimeSpan Measured(IEnumerable<ClassifiedBlock> blocks)
        => TimeSpan.FromTicks(blocks.Sum(b => b.Block.Duration.Ticks));

    private enum SlotShape
    {
        /// <summary>A genuinely contiguous stretch — its real start and end are the truth.</summary>
        Session,

        /// <summary>Snatches of one target gathered from across the day.</summary>
        Pooled,

        /// <summary>Everything too small to name, gathered together.</summary>
        OddsAndEnds,
    }

    private static SuggestionSlot ToSlot(List<ClassifiedBlock> blocks, SuggestionSlotOptions opts, SlotShape shape)
    {
        var oddsAndEnds = shape == SlotShape.OddsAndEnds;
        var measured = Measured(blocks);
        var reported = Round(measured, opts.Rounding);
        var start = blocks.Min(b => b.Block.Start);

        // A pooled slot was never one stretch of time, so giving it the span from its first snatch
        // to its last would draw a card across hours the work didn't occupy. It's shown at the
        // time it started, as long as the time it earned — the honest shape for scattered work.
        var end = shape == SlotShape.Session ? blocks.Max(b => b.Block.End) : start + reported;

        return new SuggestionSlot(
            Category: oddsAndEnds ? OddsAndEndsCategory : DominantBy(blocks, b => b.Classification.Category),
            TicketRef: oddsAndEnds ? null : blocks.Select(b => b.EffectiveTicket).FirstOrDefault(t => t is not null),
            Label: oddsAndEnds ? OddsAndEndsCategory : Label(blocks, blocks.Select(b => b.EffectiveTicket).FirstOrDefault(t => t is not null)),
            Start: start,
            End: end,
            Measured: measured,
            Reported: reported,
            Blocks: blocks,
            IsOddsAndEnds: oddsAndEnds);
    }

    // What the line says it was: the activity the most time went to, which for a ticket slot is
    // whichever window (the Halo tab, the remote session) dominated it.
    private static string Label(List<ClassifiedBlock> blocks, string? ticket)
    {
        var subject = DominantBy(blocks.Where(b => b.Classification.Subject is not null).ToList(),
            b => b.Classification.Subject!);
        if (subject.Length > 0)
            return subject;

        var title = DominantBy(blocks, b => TitleNormalizer.Normalize(b.Block.Title));
        if (title.Length > 0)
            return title;

        return ticket is not null ? $"Ticket #{ticket}" : DominantBy(blocks, b => b.Classification.Category);
    }

    // The value the most block time went to — the honest representative of a mixed group.
    private static string DominantBy(
        IReadOnlyList<ClassifiedBlock> blocks, Func<ClassifiedBlock, string> select)
        => blocks
            .Select(b => (Value: select(b), b.Block.Duration))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Value)
            .OrderByDescending(g => g.Sum(x => x.Duration.Ticks))
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;
}
