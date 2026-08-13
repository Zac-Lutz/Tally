using System.Globalization;

namespace Tally.Core;

/// <summary>
/// Formatting shared by the Markdown and HTML report writers — and by the app wherever it names a
/// duration or a time back to the user, so a confirmation dialog reads the same as the table it
/// was opened from.
/// </summary>
public static class ReportFormat
{

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1
            ? $"{(int)t.TotalMinutes}m"
            : $"{t.Seconds}s";

    /// <summary>
    /// Local-time clock in 12-hour form with a lowercase meridiem (e.g. 2:00pm, 9:05am).
    /// Stored timestamps are UTC; reports read in local time. InvariantCulture keeps the
    /// AM/PM designators stable regardless of the machine's culture.
    /// </summary>
    public static string Clock(DateTimeOffset t)
        => t.ToLocalTime().ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>Human-facing date (MM-dd-yyyy). The JSON export keeps ISO dates separately.</summary>
    public static string DisplayDate(DateOnly d) => d.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);

    /// <summary>Merges a block's client and subject into one display cell.</summary>
    public static string Detail(string? client, string? subject) => (client, subject) switch
    {
        ({ } c, { } s) => $"{c} / {s}",
        ({ } c, null) => c,
        (null, { } s) => s,
        _ => string.Empty,
    };
}
