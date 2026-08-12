using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>
/// Watches foreground-window changes (EVENT_SYSTEM_FOREGROUND) and foreground title changes
/// (EVENT_OBJECT_NAMECHANGE — how browser tab switches surface, since the window doesn't change).
/// Construct and Start on a thread with a message pump (the WinForms UI thread).
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    public event Action<TrackedEvent>? EventCaptured;

    // Rooted in a field so the GC never collects the delegate the native hook calls back into.
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly System.Windows.Forms.Timer _titleDebounce;
    private IntPtr _foregroundHook;
    private IntPtr _nameChangeHook;
    private string? _lastProcess;
    private string? _lastTitle;

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
        _titleDebounce = new System.Windows.Forms.Timer { Interval = 1000 };
        _titleDebounce.Tick += OnTitleDebounceElapsed;
    }

    public void Start()
    {
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _nameChangeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // Seed with whatever is foreground right now.
        Emit(EventKind.Focus, NativeMethods.GetForegroundWindow());
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            _titleDebounce.Stop();
            Emit(EventKind.Focus, hwnd);
        }
        else if (eventType == NativeMethods.EVENT_OBJECT_NAMECHANGE)
        {
            // NAMECHANGE fires for every UI object everywhere; only the foreground window's
            // own title matters here.
            if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0
                || hwnd != NativeMethods.GetForegroundWindow())
            {
                return;
            }

            // Browsers emit transient titles while a page loads; let the title settle before recording.
            _titleDebounce.Stop();
            _titleDebounce.Start();
        }
    }

    private void OnTitleDebounceElapsed(object? sender, EventArgs e)
    {
        _titleDebounce.Stop();
        Emit(EventKind.TitleChange, NativeMethods.GetForegroundWindow());
    }

    private void Emit(EventKind kind, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var (processName, windowTitle) = WindowInfo.Read(hwnd);
        if (processName == _lastProcess && windowTitle == _lastTitle)
            return;

        _lastProcess = processName;
        _lastTitle = windowTitle;
        EventCaptured?.Invoke(new TrackedEvent
        {
            Timestamp = DateTimeOffset.Now,
            Kind = kind,
            ProcessName = processName,
            WindowTitle = windowTitle,
        });
    }

    public void Dispose()
    {
        _titleDebounce.Dispose();
        if (_foregroundHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_nameChangeHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_nameChangeHook);
        _foregroundHook = IntPtr.Zero;
        _nameChangeHook = IntPtr.Zero;
    }
}
