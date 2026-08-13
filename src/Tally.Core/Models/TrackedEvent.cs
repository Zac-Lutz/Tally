namespace Tally.Core.Models;

public enum EventKind
{
    /// <summary>The foreground window changed to a different window.</summary>
    Focus,

    /// <summary>The foreground window's title changed in place (e.g. a browser tab switch).</summary>
    TitleChange,

    /// <summary>
    /// No keyboard/mouse input for the configured threshold.
    /// The timestamp is backdated to the moment of the last input.
    /// </summary>
    IdleStart,

    /// <summary>Input resumed after an idle period.</summary>
    IdleEnd,

    /// <summary>The workstation was locked.</summary>
    Lock,

    /// <summary>The workstation was unlocked.</summary>
    Unlock,

    /// <summary>A process began actively capturing from a microphone.</summary>
    MicStart,

    /// <summary>A process stopped actively capturing from a microphone.</summary>
    MicEnd,

    /// <summary>
    /// Tally started recording. Marks where the watchers' knowledge begins again after a restart:
    /// anything they believed was in progress before it (a live mic span) can no longer be vouched
    /// for, because whatever happened while the app was down went unobserved.
    /// </summary>
    Startup,
}

/// <summary>One raw captured event. Window titles are stored verbatim.</summary>
public sealed class TrackedEvent
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public EventKind Kind { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
}
