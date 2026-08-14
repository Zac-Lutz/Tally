namespace Tally.Core;

/// <summary>
/// One slot placed on the day grid. <see cref="Column"/> of <see cref="Columns"/> is its share of
/// the width where slots overlap — every slot in an overlapping run gets the same column count, so
/// they sit side by side at equal widths instead of on top of each other.
/// </summary>
public sealed record CalendarEntry(SuggestionSlot Slot, int Column, int Columns);

/// <summary>
/// Lays the day's timesheet slots out as a calendar would: side by side where they overlap in time.
/// <para>
/// Slots genuinely can overlap — a meeting and the scattered work pooled around it are separate
/// entries covering the same minutes, and seeing that is the point of a calendar view rather than
/// something to hide. Placement is the standard sweep: slots that overlap form a run, and within a
/// run each takes the first column free at its start time.
/// </para>
/// </summary>
public static class TimesheetCalendar
{
    /// <summary>Blocks closer together than this draw as one visit — a flurry of tab switches
    /// inside the same minute is one sitting, not confetti.</summary>
    public static readonly TimeSpan VisitMergeGap = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The stretch of day a slot is <b>drawn</b> over. A window-activity slot revisited across the
    /// day draws as an envelope from its first visit to its last — the true story of the work —
    /// even though the slot's official <c>End</c> (and the export built from it) stays compact.
    /// Calls, timers, and the odds-and-ends slot keep their own span: a call is one stretch, and
    /// odds-and-ends is a mixed bag with no single story to stretch over.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DisplaySpan(SuggestionSlot slot)
    {
        if (slot.Kind != SuggestionSlotKind.Activity || slot.Blocks.Count == 0)
            return (slot.Start, slot.End);

        var last = slot.Blocks.Max(b => b.Block.End);
        return (slot.Start, last > slot.End ? last : slot.End);
    }

    /// <summary>
    /// The distinct sittings an activity slot's time was actually spent in: its blocks in time
    /// order, merged wherever they touch or sit within <see cref="VisitMergeGap"/> of each other.
    /// These are the solid pins inside the envelope.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Visits(SuggestionSlot slot)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var block in slot.Blocks.OrderBy(b => b.Block.Start))
        {
            var (start, end) = (block.Block.Start, block.Block.End);
            if (end <= start)
                continue;

            if (merged.Count > 0 && start - merged[^1].End <= VisitMergeGap)
                merged[^1] = (merged[^1].Start, end > merged[^1].End ? end : merged[^1].End);
            else
                merged.Add((start, end));
        }

        return merged;
    }

    public static IReadOnlyList<CalendarEntry> Lay(IReadOnlyList<SuggestionSlot> slots)
    {
        var entries = new List<CalendarEntry>();
        var run = new List<(SuggestionSlot Slot, int Column)>();
        var columnEnds = new List<DateTimeOffset>();
        var runEnd = DateTimeOffset.MinValue;

        void CloseRun()
        {
            foreach (var (slot, column) in run)
                entries.Add(new CalendarEntry(slot, column, columnEnds.Count));
            run.Clear();
            columnEnds.Clear();
            runEnd = DateTimeOffset.MinValue;
        }

        // Overlap is judged on what will be DRAWN — the envelope — so a stretched slot shares
        // width with everything it visually crosses.
        foreach (var slot in slots
                     .OrderBy(s => DisplaySpan(s).Start)
                     .ThenByDescending(s => DisplaySpan(s).End))
        {
            var (start, end) = DisplaySpan(slot);

            // Nothing still open reaches this slot, so the previous run is finished and the next
            // one starts back at full width.
            if (run.Count > 0 && start >= runEnd)
                CloseRun();

            var column = columnEnds.FindIndex(e => e <= start);
            if (column < 0)
            {
                columnEnds.Add(end);
                column = columnEnds.Count - 1;
            }
            else
            {
                columnEnds[column] = end;
            }

            run.Add((slot, column));
            if (end > runEnd)
                runEnd = end;
        }

        CloseRun();
        return entries.OrderBy(e => e.Slot.Start).ToList();
    }

    /// <summary>The grid's ruling interval: lines every half hour, labelled on the hour.</summary>
    public static readonly TimeSpan GridStep = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The stretch of day the grid should span — the slots rounded out to the nearest ruling line,
    /// so nothing sits flush against an edge and no more than half an hour of empty grid is drawn
    /// above the first entry. Returns null for an empty day.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? Bounds(IReadOnlyList<SuggestionSlot> slots)
    {
        if (slots.Count == 0)
            return null;

        var spans = slots.Select(DisplaySpan).ToList();
        var start = Floor(spans.Min(s => s.Start));
        var end = Floor(spans.Max(s => s.End));
        if (end < spans.Max(s => s.End))
            end += GridStep;

        // A day whose work all sits inside one interval still needs a grid to sit on.
        return end <= start ? (start, start + GridStep) : (start, end);
    }

    private static DateTimeOffset Floor(DateTimeOffset value)
        => value.AddTicks(-(value.Ticks % GridStep.Ticks));
}
