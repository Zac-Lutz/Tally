using Microsoft.Win32;
using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>Emits Lock/Unlock from workstation session switches. Start from a message-pump thread.</summary>
public sealed class SessionWatcher : IDisposable
{
    public event Action<TrackedEvent>? EventCaptured;

    private bool _started;

    public void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _started = true;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        EventKind? kind = e.Reason switch
        {
            SessionSwitchReason.SessionLock => EventKind.Lock,
            SessionSwitchReason.SessionUnlock => EventKind.Unlock,
            _ => null,
        };

        if (kind is { } k)
            EventCaptured?.Invoke(new TrackedEvent { Timestamp = DateTimeOffset.Now, Kind = k });
    }

    public void Dispose()
    {
        if (_started)
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        _started = false;
    }
}
