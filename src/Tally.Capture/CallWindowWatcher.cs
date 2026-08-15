using Tally.Core;
using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>
/// Polls every top-level window on the desktop and emits CallWindowOpen/CallWindowClose for the
/// ones that are call windows — a Teams meeting, at present.
/// <para>
/// This is the answer to a meeting that recorded fifteen minutes of an hour. The only witness to a
/// call used to be the microphone, which reports whether you are <em>talking</em>. Mute yourself to
/// listen and Teams hands the microphone back, so the call ended as far as Tally was concerned and
/// the rest of the meeting was filed as whatever window you glanced at next.
/// </para>
/// <para>
/// The foreground watcher can't answer this either: it only ever sees the window being looked at,
/// and the whole point of a meeting is that you look at other things during it. So this enumerates
/// <b>all</b> windows rather than the focused one, and a meeting counts while its window exists
/// anywhere on the desktop — minimised, behind everything, on another monitor.
/// </para>
/// </summary>
public sealed class CallWindowWatcher : IDisposable
{
    public event Action<TrackedEvent>? EventCaptured;

    private readonly System.Threading.Timer _timer;

    // Keyed by process, valued by meeting name. One meeting per app at a time: you cannot be in two
    // Teams meetings at once, and treating a rename as a new meeting would split one call in half.
    private Dictionary<string, string> _openCalls = new(StringComparer.OrdinalIgnoreCase);
    private int _polling;

    public CallWindowWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            var nowOpen = ReadCallWindows();
            if (nowOpen is null)
                return;   // couldn't read the desktop — keep the previous state rather than ending a live call

            var timestamp = DateTimeOffset.Now;

            foreach (var (process, meeting) in nowOpen)
            {
                // A meeting whose name changed is the same meeting under a new title, not a new
                // one: Teams renames its window as you join, then again when it shrinks to the
                // compact view. Only a genuinely different name is a different meeting, and that
                // closes the old one before opening the new.
                if (_openCalls.TryGetValue(process, out var was))
                {
                    if (string.Equals(was, meeting, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Emit(EventKind.CallWindowClose, timestamp, process, was);
                }

                Emit(EventKind.CallWindowOpen, timestamp, process, meeting);
            }

            foreach (var (process, meeting) in _openCalls)
            {
                if (!nowOpen.ContainsKey(process))
                    Emit(EventKind.CallWindowClose, timestamp, process, meeting);
            }

            _openCalls = nowOpen;
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void Emit(EventKind kind, DateTimeOffset timestamp, string process, string meeting)
        => EventCaptured?.Invoke(new TrackedEvent
        {
            Timestamp = timestamp,
            Kind = kind,
            ProcessName = process,
            WindowTitle = meeting,
        });

    /// <summary>
    /// Every call window currently on the desktop, as process → meeting name. Null when the
    /// enumeration itself failed, which is deliberately different from "no calls": an empty result
    /// ends every open call, and a transient failure must not be allowed to do that.
    /// </summary>
    private static Dictionary<string, string>? ReadCallWindows()
    {
        try
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    try
                    {
                        // Invisible and untitled windows are the app's plumbing, not a meeting.
                        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowTextLength(hwnd) == 0)
                            return true;

                        var (process, title) = WindowInfo.Read(hwnd);
                        if (CallApps.MeetingName(process, title) is { } meeting)
                            found[process] = meeting;
                    }
                    catch (Exception)
                    {
                        // One unreadable window shouldn't stop the sweep.
                    }

                    return true;
                },
                IntPtr.Zero);

            return found;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _timer.Dispose();
}
