using Tally.Core.Models;

namespace Tally.Core;

/// <summary>What a slot's time is evidenced by, in the order that outranks.</summary>
public enum SuggestionSlotKind
{
    /// <summary>Foreground window activity — a ticket or a category.</summary>
    Activity,

    /// <summary>A call or meeting, from the mic lane.</summary>
    Call,

    /// <summary>A manual timer the user started deliberately.</summary>
    Timer,

    /// <summary>Everything too small to stand alone, combined.</summary>
    OddsAndEnds,
}

/// <summary>
/// One proposed timesheet entry. <see cref="Measured"/> is the time actually observed;
/// <see cref="Reported"/> is what goes on the timesheet after rounding. They're kept apart so a
/// verification screen can show both and the rounding never hides from the person reviewing it.
/// <see cref="Blocks"/> is the window activity underneath the slot — for a call or timer that's
/// what was on screen during it, which is detail, not the source of its time.
/// </summary>
public sealed record SuggestionSlot(
    SuggestionSlotKind Kind,
    string Category,
    string? TicketRef,
    string Label,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Measured,
    TimeSpan Reported,
    IReadOnlyList<ClassifiedBlock> Blocks);

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

    /// <summary>
    /// Keep only slots that <b>begin</b> at or after this local time. Null covers the whole day.
    /// </summary>
    public TimeOnly? WindowStart { get; init; }

    /// <summary>Keep only slots that <b>begin</b> before this local time. Null covers the whole day.</summary>
    public TimeOnly? WindowEnd { get; init; }
}

/// <summary>
/// Turns a classified day into the handful of slots a timesheet actually wants.
/// <para>
/// <b>Time is claimed in priority order, so no minute is billed twice.</b> A manual timer outranks
/// everything — you started it deliberately, so it's the strongest statement of what that time was.
/// A call outranks window activity, because an hour in a meeting is an hour of meeting even though
/// you were reading a ticket through it; without this, meetings dissolve into whatever happened to
/// be on screen. Window activity gets what's left.
/// </para>
/// <para>
/// Window activity groups by <b>billing target</b> — a ticket if one was detected, otherwise the
/// category — split into sessions, so a ticket worked morning and afternoon is two entries sitting
/// where the work happened rather than one card smeared across the day.
/// </para>
/// <para>
/// Nothing is thrown away for being short, because losing ten two-minute visits loses twenty real
/// minutes. Anything under the minimum goes through two rescues: the day's leftovers re-pool per
/// target (six two-minute visits to one ticket become one twelve-minute entry), and whatever is
/// still too small to stand alone combines into a single "odds and ends" slot carrying all of its
/// detail. Only that slot is unattributable, and it's visible rather than silently missing.
/// </para>
/// </summary>
public static class SuggestionSlotBuilder
{
    /// <summary>The category the combined leftovers slot carries.</summary>
    public const string OddsAndEndsCategory = "Odds and ends";

    private readonly record struct Span(DateTimeOffset Start, DateTimeOffset End)
    {
        public TimeSpan Duration => End - Start;
    }

    public static IReadOnlyList<SuggestionSlot> Build(
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan>? calls = null,
        IReadOnlyList<ManualTimer>? timers = null,
        SuggestionSlotOptions? options = null)
    {
        var opts = options ?? new SuggestionSlotOptions();
        var slots = new List<SuggestionSlot>();

        // 1. Timers claim first — the most deliberate signal there is.
        var timerSpans = (timers ?? [])
            .Where(t => t.End > t.Start)
            .Select(t => new Span(t.Start, t.End))
            .ToList();
        var claimed = Merge(timerSpans);

        foreach (var timer in (timers ?? []).Where(t => t.End > t.Start))
        {
            slots.Add(ToClaimSlot(
                SuggestionSlotKind.Timer, RollupBuilder.TimerCategory, timer.Name,
                new Span(timer.Start, timer.End), timer.End - timer.Start, blocks, opts));
        }

        // 2. Calls claim what the timers left. A call reduced below the minimum claims nothing —
        //    its handful of seconds isn't worth a line, and leaving the time to the window lane
        //    keeps it counted.
        foreach (var call in (calls ?? []).Where(c => c.End > c.Start))
        {
            var remaining = Subtract(new Span(call.Start, call.End), claimed);
            var measured = Total(remaining);
            if (measured < opts.MinimumSlot)
                continue;

            slots.Add(ToClaimSlot(
                SuggestionSlotKind.Call, RollupBuilder.CallCategory,
                call.Title.Length > 0 ? call.Title : call.ProcessName,
                new Span(call.Start, call.End), measured, blocks, opts));
            claimed = Merge(claimed.Concat(remaining));
        }

        // 3. Window activity gets the time nothing above it claimed.
        var free = blocks
            .Where(b => b.Block.Duration > TimeSpan.Zero)
            .SelectMany(b => Clip(b, claimed))
            .ToList();

        slots.AddRange(ActivitySlots(free, opts));

        return slots
            .Where(s => InWindow(s, opts.WindowStart, opts.WindowEnd))
            .OrderBy(s => s.Start)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether a slot belongs to an export window. Membership is decided by where a slot
    /// <b>begins</b>, never by overlap: a meeting running through the cut-off belongs to the half
    /// of the day it started in, so exporting a morning and then an afternoon covers everything
    /// exactly once instead of billing the straddler twice.
    /// </summary>
    public static bool InWindow(SuggestionSlot slot, TimeOnly? from, TimeOnly? to)
    {
        if (from is null && to is null)
            return true;

        var at = TimeOnly.FromDateTime(slot.Start.ToLocalTime().DateTime);
        return (from is null || at >= from) && (to is null || at < to);
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

    private static List<SuggestionSlot> ActivitySlots(List<ClassifiedBlock> free, SuggestionSlotOptions opts)
    {
        var slots = new List<SuggestionSlot>();
        var leftovers = new List<List<ClassifiedBlock>>();

        foreach (var session in free
                     .GroupBy(TargetKey)
                     .SelectMany(t => Sessions(t.OrderBy(b => b.Block.Start).ToList(), opts.SessionGap)))
        {
            if (Measured(session) >= opts.MinimumSlot)
                slots.Add(ToActivitySlot(session, opts, SuggestionSlotKind.Activity, contiguous: true));
            else
                leftovers.Add(session);
        }

        // Rescue 1 — the same target, revisited all day in snatches, is one entry's worth of work.
        var scattered = new List<ClassifiedBlock>();
        foreach (var target in leftovers.SelectMany(s => s).GroupBy(TargetKey))
        {
            var pooled = target.OrderBy(b => b.Block.Start).ToList();
            if (Measured(pooled) >= opts.MinimumSlot)
                slots.Add(ToActivitySlot(pooled, opts, SuggestionSlotKind.Activity, contiguous: false));
            else
                scattered.AddRange(pooled);
        }

        // Rescue 2 — everything still too small to name, kept as one visible line rather than lost.
        if (scattered.Count > 0 && Measured(scattered) >= opts.MinimumOddsAndEnds)
        {
            slots.Add(ToActivitySlot(
                scattered.OrderBy(b => b.Block.Start).ToList(), opts,
                SuggestionSlotKind.OddsAndEnds, contiguous: false));
        }

        return slots;
    }

    // A call or timer slot: its time is the claim's, and the window activity underneath it rides
    // along as detail — what was on screen during the meeting is exactly what makes the note
    // writable later.
    private static SuggestionSlot ToClaimSlot(
        SuggestionSlotKind kind, string category, string label, Span span, TimeSpan measured,
        IReadOnlyList<ClassifiedBlock> allBlocks, SuggestionSlotOptions opts)
        => new(
            Kind: kind,
            Category: category,
            TicketRef: null,
            Label: string.IsNullOrWhiteSpace(label) ? category : label,
            Start: span.Start,
            End: span.End,
            Measured: measured,
            Reported: Round(measured, opts.Rounding),
            Blocks: allBlocks.SelectMany(b => Overlap(b, span)).ToList());

    private static SuggestionSlot ToActivitySlot(
        List<ClassifiedBlock> blocks, SuggestionSlotOptions opts, SuggestionSlotKind kind, bool contiguous)
    {
        var oddsAndEnds = kind == SuggestionSlotKind.OddsAndEnds;
        var measured = Measured(blocks);
        var reported = Round(measured, opts.Rounding);
        var start = blocks.Min(b => b.Block.Start);
        var ticket = oddsAndEnds ? null : blocks.Select(b => b.EffectiveTicket).FirstOrDefault(t => t is not null);

        // Pooled work was never one stretch of time, so giving it the span from its first snatch to
        // its last would draw a card across hours the work didn't occupy. It's shown at the time it
        // started, as long as the time it earned — the honest shape for scattered work.
        var end = contiguous ? blocks.Max(b => b.Block.End) : start + reported;

        return new SuggestionSlot(
            Kind: kind,
            Category: oddsAndEnds ? OddsAndEndsCategory : DominantBy(blocks, b => b.Classification.Category),
            TicketRef: ticket,
            Label: oddsAndEnds ? OddsAndEndsCategory : Label(blocks, ticket),
            Start: start,
            End: end,
            Measured: measured,
            Reported: reported,
            Blocks: blocks);
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

    // What the line says it was: the activity the most time went to, which for a ticket slot is
    // whichever window (the Halo tab, the remote session) dominated it.
    private static string Label(List<ClassifiedBlock> blocks, string? ticket)
    {
        var subject = DominantBy(
            blocks.Where(b => b.Classification.Subject is not null).ToList(), b => b.Classification.Subject!);
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

    // ---------- Interval arithmetic ----------

    private static List<Span> Merge(IEnumerable<Span> spans)
    {
        var merged = new List<Span>();
        foreach (var span in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = span.End > merged[^1].End ? span.End : merged[^1].End };
            else
                merged.Add(span);
        }

        return merged;
    }

    /// <summary>The parts of <paramref name="span"/> that no claim covers. Claims must be merged.</summary>
    private static List<Span> Subtract(Span span, IReadOnlyList<Span> claims)
    {
        var remaining = new List<Span>();
        var cursor = span.Start;
        foreach (var claim in claims.Where(c => c.End > span.Start && c.Start < span.End))
        {
            if (claim.Start > cursor)
                remaining.Add(new Span(cursor, claim.Start));
            if (claim.End > cursor)
                cursor = claim.End;
            if (cursor >= span.End)
                break;
        }

        if (cursor < span.End)
            remaining.Add(new Span(cursor, span.End));

        return remaining;
    }

    private static TimeSpan Total(IEnumerable<Span> spans)
        => TimeSpan.FromTicks(spans.Sum(s => s.Duration.Ticks));

    /// <summary>The block, cut down to the time no higher-priority lane claimed.</summary>
    private static IEnumerable<ClassifiedBlock> Clip(ClassifiedBlock block, IReadOnlyList<Span> claims)
        => Subtract(new Span(block.Block.Start, block.Block.End), claims)
            .Select(piece => block with { Block = block.Block with { Start = piece.Start, End = piece.End } });

    /// <summary>The part of the block that falls inside a claim — the detail beneath a call/timer.</summary>
    private static IEnumerable<ClassifiedBlock> Overlap(ClassifiedBlock block, Span span)
    {
        var start = block.Block.Start > span.Start ? block.Block.Start : span.Start;
        var end = block.Block.End < span.End ? block.Block.End : span.End;
        if (end > start)
            yield return block with { Block = block.Block with { Start = start, End = end } };
    }
}
