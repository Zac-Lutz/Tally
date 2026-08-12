namespace Tally.App;

internal static class TimerText
{
    /// <summary>Formats a running elapsed time as a stopwatch: MM:SS, or H:MM:SS past an hour.</summary>
    public static string Elapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
