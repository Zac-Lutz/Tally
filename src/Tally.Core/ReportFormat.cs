namespace Tally.Core;

/// <summary>Formatting shared by the Markdown and HTML report writers.</summary>
internal static class ReportFormat
{
    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1
            ? $"{(int)t.TotalMinutes}m"
            : $"{t.Seconds}s";

    /// <summary>Local-time clock (HH:mm). Stored timestamps are UTC; reports read in local time.</summary>
    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm");

    /// <summary>Merges a block's client and subject into one display cell.</summary>
    public static string Detail(string? client, string? subject) => (client, subject) switch
    {
        ({ } c, { } s) => $"{c} / {s}",
        ({ } c, null) => c,
        (null, { } s) => s,
        _ => string.Empty,
    };
}
