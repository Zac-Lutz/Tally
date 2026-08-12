using System.Runtime.InteropServices;
using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>
/// Polls GetLastInputInfo and emits IdleStart (backdated to the last input) / IdleEnd.
/// Construct on the UI thread (WinForms timer).
/// </summary>
public sealed class IdleWatcher : IDisposable
{
    public event Action<TrackedEvent>? EventCaptured;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly TimeSpan _threshold;
    private bool _isIdle;

    public IdleWatcher(TimeSpan? threshold = null)
    {
        _threshold = threshold ?? TimeSpan.FromMinutes(4);
        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        var idleFor = GetIdleDuration();
        if (!_isIdle && idleFor >= _threshold)
        {
            _isIdle = true;
            EventCaptured?.Invoke(new TrackedEvent
            {
                Timestamp = DateTimeOffset.Now - idleFor,   // backdate to the last input
                Kind = EventKind.IdleStart,
            });
        }
        else if (_isIdle && idleFor < _threshold)
        {
            _isIdle = false;
            EventCaptured?.Invoke(new TrackedEvent
            {
                Timestamp = DateTimeOffset.Now - idleFor,
                Kind = EventKind.IdleEnd,
            });
        }
    }

    private static TimeSpan GetIdleDuration()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        // TickCount subtraction as int is wrap-safe for spans under ~24.9 days.
        var elapsedMs = unchecked(Environment.TickCount - (int)info.dwTime);
        return elapsedMs > 0 ? TimeSpan.FromMilliseconds(elapsedMs) : TimeSpan.Zero;
    }

    public void Dispose() => _timer.Dispose();
}
