namespace Tally.Core;

/// <summary>
/// The dates a month is drawn on in a calendar: six rows of seven, starting on the week that holds
/// the first. Six rows always, so a month that needs a sixth week and one that doesn't are the same
/// size — a grid that changed height between August and September would move the day under the
/// cursor as you paged.
/// </summary>
public static class MonthGrid
{
    /// <summary>Rows down, days across.</summary>
    public const int Weeks = 6;
    public const int DaysPerWeek = 7;

    /// <summary>
    /// The 42 dates of the grid for <paramref name="month"/>, in reading order. Dates outside the
    /// month are included — a caller draws them as blanks or as the neighbouring month, but they
    /// have to exist for the grid to line up under its weekday headings.
    /// </summary>
    public static IReadOnlyList<DateOnly> Build(int year, int month, DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
    {
        var first = new DateOnly(year, month, 1);
        var lead = ((int)first.DayOfWeek - (int)firstDayOfWeek + DaysPerWeek) % DaysPerWeek;
        var start = first.AddDays(-lead);
        var cells = new DateOnly[Weeks * DaysPerWeek];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = start.AddDays(i);

        return cells;
    }

    /// <summary>The weekday headings in the order <see cref="Build"/> lays days out.</summary>
    public static IReadOnlyList<DayOfWeek> Headings(DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
    {
        var days = new DayOfWeek[DaysPerWeek];
        for (var i = 0; i < DaysPerWeek; i++)
            days[i] = (DayOfWeek)(((int)firstDayOfWeek + i) % DaysPerWeek);

        return days;
    }
}
