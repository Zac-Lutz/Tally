namespace Tally.Core;

/// <summary>
/// What the Timers tab's start/stop control should show: the name in its field, and the moment a
/// running timer began (null when nothing is running). Handed to the writer rather than read from
/// <see cref="ManualTimerService"/> directly, so the renderer stays a pure function of its inputs
/// and can be exercised without a running timer.
/// </summary>
public sealed record TimerPanelState(string Name, DateTimeOffset? StartedAt, TimeSpan Elapsed = default);
