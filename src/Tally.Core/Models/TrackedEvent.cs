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
    /// A call window appeared — a Teams meeting window, wherever it sits and whether or not it is
    /// the window being looked at. <see cref="TrackedEvent.WindowTitle"/> carries the meeting name.
    /// <para>
    /// This exists because the microphone answers the wrong question: it says whether you are
    /// talking, not whether you are in a meeting. Muted time is still meeting time.
    /// </para>
    /// </summary>
    CallWindowOpen,

    /// <summary>A call window went away — the meeting was left, or Tally stopped being able to see it.</summary>
    CallWindowClose,

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

    /// <summary>The page a browser window showed, as sanitized host/path — null for everything
    /// that isn't a browser, and for address-bar text that wasn't a page.</summary>
    public string? Url { get; set; }
}
