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
/// Input-activity counts attributed to a block. Keystroke/click COUNTS only — never which
/// keys — an intensity signal that separates active work from a window left open.
/// </summary>
public sealed record BlockActivity(int Keystrokes, int MouseClicks)
{
    public static readonly BlockActivity None = new(0, 0);

    public int Total => Keystrokes + MouseClicks;
}

public sealed record ClassifiedBlock(Block Block, Classification Classification, BlockActivity Activity);
