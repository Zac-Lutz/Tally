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

public sealed record Classification(string Category, string? Client, string? TicketRef, string? RuleId)
{
    public const string Unclassified = "Unclassified";

    public bool IsUnclassified => Category == Unclassified;
}

public sealed record ClassifiedBlock(Block Block, Classification Classification);
