using Tally.Core.Models;

namespace Tally.Core;

/// <summary>A running manual timer.</summary>
public sealed record ActiveTimer(string Name, DateTimeOffset StartedAt);

/// <summary>
/// Tracks the single active manual timer (start/stop/rename) and hands each completed span to a
/// persistence callback. UI-agnostic and deterministic (time is injected), so it's unit-tested;
/// the WinForms UI, hotkeys, and DB persistence wire into its <see cref="Changed"/> event and
/// the <c>onCompleted</c> callback.
/// </summary>
public sealed class ManualTimerService
{
    public const string DefaultName = "Untitled timer";

    private readonly Action<ManualTimer> _onCompleted;
    private readonly Func<DateTimeOffset> _now;

    public ManualTimerService(Action<ManualTimer> onCompleted, Func<DateTimeOffset>? now = null)
    {
        _onCompleted = onCompleted;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public ActiveTimer? Active { get; private set; }

    public bool IsActive => Active is not null;

    /// <summary>Name used for the next <see cref="Start"/>; the UI keeps its name field bound to this.</summary>
    public string PendingName { get; set; } = string.Empty;

    /// <summary>Raised after any state change (start/stop/rename) so the UI can refresh.</summary>
    public event Action? Changed;

    public TimeSpan Elapsed => Active is { } a ? _now() - a.StartedAt : TimeSpan.Zero;

    /// <summary>Starts a timer. Any running timer is stopped (and persisted) first.</summary>
    public void Start(string? name = null)
    {
        if (name is not null)
            PendingName = name;

        CompleteActive();
        Active = new ActiveTimer(Normalize(PendingName), _now());
        Changed?.Invoke();
    }

    /// <summary>Stops and persists the running timer, if any.</summary>
    public void Stop()
    {
        if (Active is null)
            return;

        CompleteActive();
        Active = null;
        Changed?.Invoke();
    }

    public void Toggle(string? name = null)
    {
        if (IsActive)
            Stop();
        else
            Start(name);
    }

    /// <summary>Sets the pending name and, if a timer is running, renames it live.</summary>
    public void Rename(string name)
    {
        PendingName = name;
        if (Active is { } a)
            Active = a with { Name = Normalize(name) };
        Changed?.Invoke();
    }

    private void CompleteActive()
    {
        if (Active is not { } a)
            return;

        var end = _now();
        if (end > a.StartedAt)
            _onCompleted(new ManualTimer { Name = a.Name, Start = a.StartedAt, End = end });
    }

    private static string Normalize(string name)
        => string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
}
