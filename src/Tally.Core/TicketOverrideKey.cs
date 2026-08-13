namespace Tally.Core;

/// <summary>
/// The stable identity of a Rollup activity, used to key a per-day manual ticket override.
/// Computed the SAME way from a rendered rollup row (<see cref="ForRow"/>) and from a classified
/// block (<see cref="ForBlock"/>) so an override typed on a row re-applies to the right blocks
/// when the day is recomputed. Always built from the ORIGINAL (auto) ticket, never the override,
/// so the key doesn't move once a ticket is entered.
/// </summary>
public static class TicketOverrideKey
{
    // Unit-separator symbol — vanishingly unlikely to appear in a window title/category.
    private const string Sep = "␟";

    /// <summary>Key for a rollup row. <paramref name="detailName"/> is the row's Detail text, which
    /// for a non-ticketed row equals its grouping activity key.</summary>
    public static string ForRow(string category, string? originalTicket, string detailName)
        => Compose(category, originalTicket, detailName);

    /// <summary>Key for a classified block, using its original classification.</summary>
    public static string ForBlock(string category, string? originalTicket, string? subject, string title)
        => Compose(category, originalTicket, subject ?? TitleNormalizer.Normalize(title));

    private static string Compose(string category, string? originalTicket, string activity)
        => originalTicket is not null
            ? $"{category}{Sep}T{Sep}{originalTicket}"
            : $"{category}{Sep}A{Sep}{activity}";
}
