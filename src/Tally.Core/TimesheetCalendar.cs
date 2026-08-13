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

        foreach (var slot in slots.OrderBy(s => s.Start).ThenByDescending(s => s.End))
        {
            // Nothing still open reaches this slot, so the previous run is finished and the next
            // one starts back at full width.
            if (run.Count > 0 && slot.Start >= runEnd)
                CloseRun();

            var column = columnEnds.FindIndex(end => end <= slot.Start);
            if (column < 0)
            {
                columnEnds.Add(slot.End);
                column = columnEnds.Count - 1;
            }
            else
            {
                columnEnds[column] = slot.End;
            }

            run.Add((slot, column));
            if (slot.End > runEnd)
                runEnd = slot.End;
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

        var start = Floor(slots.Min(s => s.Start));
        var end = Floor(slots.Max(s => s.End));
        if (end < slots.Max(s => s.End))
            end += GridStep;

        // A day whose work all sits inside one interval still needs a grid to sit on.
        return end <= start ? (start, start + GridStep) : (start, end);
    }

    private static DateTimeOffset Floor(DateTimeOffset value)
        => value.AddTicks(-(value.Ticks % GridStep.Ticks));
}
