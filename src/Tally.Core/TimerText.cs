namespace Tally.Core;

/// <summary>
/// How a running timer's elapsed time reads. Lives in Core because it's shown in three places that
/// must agree — the live view's top bar, the floating bubble, and the Timers tab (whose script
/// ticks between refreshes and reproduces this same format).
/// </summary>
public static class TimerText
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
