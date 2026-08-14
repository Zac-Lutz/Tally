namespace Tally.Core;

/// <summary>
/// Decides what "old enough to purge" means for raw events. Retention is measured in whole local
/// days: the cutoff is local midnight <c>retentionDays</c> days before today, so a purge only ever
/// removes complete days and every day inside the window stays fully regenerable.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>The floor for a positive retention: the last week must always stay regenerable.</summary>
    public const int MinimumDays = 7;

    /// <summary>
    /// The instant strictly before which raw events may be deleted, or null when
    /// <paramref name="retentionDays"/> is zero or negative (keep everything forever).
    /// Positive values below <see cref="MinimumDays"/> are raised to it.
    /// </summary>
    public static DateTimeOffset? Cutoff(DateOnly today, int retentionDays)
    {
        if (retentionDays <= 0)
            return null;

        var days = Math.Max(retentionDays, MinimumDays);
        return new DateTimeOffset(today.AddDays(-days).ToDateTime(TimeOnly.MinValue));
    }
}
