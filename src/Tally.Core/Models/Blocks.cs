namespace Tally.Core.Models;

/// <summary>A contiguous stretch of foreground time in one window context. The title is kept verbatim.</summary>
public sealed record Block(DateTimeOffset Start, DateTimeOffset End, string ProcessName, string Title)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// A stretch of time a process was actively capturing the microphone (a call).
/// Call spans overlay foreground blocks — they run in their own lane and do not
/// compete with whatever window was focused during the call.
/// </summary>
public sealed record CallSpan(DateTimeOffset Start, DateTimeOffset End, string ProcessName, string Title)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Idle or locked time, excluded from foreground blocks.</summary>
public sealed record InactivePeriod(DateTimeOffset Start, DateTimeOffset End, string Reason)
{
    public TimeSpan Duration => End - Start;
}

public static class InactiveReasons
{
    public const string Idle = "idle";
    public const string Locked = "locked";
}

/// <summary>
/// The classification of a block. <see cref="Subject"/> is a free-text "what/who" the block
/// was about (a Teams chat name, a document) captured by a rule's <c>(?&lt;subject&gt;)</c> group,
/// distinct from <see cref="Client"/> (an organization) and <see cref="TicketRef"/>.
/// </summary>
public sealed record Classification(
    string Category, string? Client, string? TicketRef, string? Subject, string? RuleId)
{
    public const string Unclassified = "Unclassified";

    public bool IsUnclassified => Category == Unclassified;
}

/// <summary>
/// A classified block, plus an optional per-day manual ticket the user typed into the Rollup's
/// Ticket cell. <see cref="OverrideTicket"/> is kept separate from the auto-detected
/// <see cref="Classification"/>.<see cref="Classification.TicketRef"/> so the block's original
/// identity stays stable (grouping and the override key don't shift when a ticket is entered).
/// <see cref="EffectiveTicket"/> is what reports and the export should show.
/// </summary>
public sealed record ClassifiedBlock(Block Block, Classification Classification, string? OverrideTicket = null)
{
    public string? EffectiveTicket => OverrideTicket ?? Classification.TicketRef;
}
