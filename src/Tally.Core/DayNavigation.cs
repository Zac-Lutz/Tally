namespace Tally.Core;

/// <summary>
/// Which day the dashboard is showing, and the rules for moving between days. The live view owns
/// the buttons; this owns what they are allowed to do and what the date reads as, so both are
/// testable without a window — and so the arrows, the calendar, and the label can never disagree
/// about which days exist.
/// </summary>
public static class DayNavigation
{
    /// <summary>
    /// <paramref name="date"/> brought inside the days that can be shown: never after today
    /// (tomorrow hasn't happened) and never before <paramref name="earliest"/> — the first day
    /// still on record, since retention eventually purges the raw events a day is rebuilt from.
    /// An <paramref name="earliest"/> after today (an empty database reads as today, a clock that
    /// moved backwards) collapses to today rather than producing an empty range.
    /// </summary>
    public static DateOnly Clamp(DateOnly date, DateOnly earliest, DateOnly today)
    {
        var floor = Floor(earliest, today);
        if (date > today)
            return today;

        return date < floor ? floor : date;
    }

    /// <summary>Whether there is an earlier day left to show.</summary>
    public static bool CanGoBack(DateOnly date, DateOnly earliest, DateOnly today)
        => date > Floor(earliest, today);

    /// <summary>Whether there is a later day to show — false on today, which is as far as time goes.</summary>
    public static bool CanGoForward(DateOnly date, DateOnly today) => date < today;

    /// <summary>
    /// The day <paramref name="days"/> away, clamped to what exists. Stepping off either end
    /// stops at the end rather than doing nothing, so holding the arrow can't get stuck.
    /// </summary>
    public static DateOnly Step(DateOnly date, int days, DateOnly earliest, DateOnly today)
        => Clamp(date.AddDays(days), earliest, today);

    /// <summary>
    /// How the date reads in the chrome: "Today · 08-16-2026", "Yesterday · 08-15-2026", or
    /// "Friday · 08-14-2026". The relative word carries the meaning and the date settles it, so
    /// the label answers "which day is this" without needing a calendar open beside it.
    /// </summary>
    public static string Label(DateOnly date, DateOnly today)
    {
        var word = date == today ? "Today"
            : date == today.AddDays(-1) ? "Yesterday"
            : date.DayOfWeek.ToString();
        return $"{word} · {ReportFormat.DisplayDate(date)}";
    }

    private static DateOnly Floor(DateOnly earliest, DateOnly today) => earliest > today ? today : earliest;
}
