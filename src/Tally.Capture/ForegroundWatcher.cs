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

    // Clicking back into a browser window lands on whatever tab was last open there, and the
    // address bar updates immediately while the window title lags. Reading the title at the focus
    // event therefore names the *previous* tab, and it gets stored against the new tab's URL —
    // which is how a third of browser focus events came to disagree with themselves.
    // Measured against a fortnight of captured events, that lag lands within 100ms about seven
    // times in ten and never exceeded 500ms, so half a second settles it. The cost is delivery
    // delay only: the event keeps the timestamp of the moment focus actually changed.
    private const int TitleSettleMs = 500;

    // Rooted in a field so the GC never collects the delegate the native hook calls back into.
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly System.Windows.Forms.Timer _titleDebounce;
    private readonly Lock _lastSeenGate = new();
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
        var timestamp = DateTimeOffset.Now;

        // Everything but a browser reports its title accurately the moment it takes the
        // foreground, so record it right here and keep the callback fast.
        if (!BrowserUrlReader.IsBrowser(processName))
        {
            if (IsRepeat(processName, windowTitle))
                return;

            EventCaptured?.Invoke(new TrackedEvent
            {
                Timestamp = timestamp,
                Kind = kind,
                ProcessName = processName,
                WindowTitle = windowTitle,
                Url = null,
            });
            return;
        }

        // A browser event also gets the page from the address bar, and a UI Automation query is
        // not something a WinEvent callback may block on — so the rest happens on the thread pool.
        // The timestamp is already stamped and readers order by timestamp, so delayed delivery
        // changes nothing about when the event is considered to have happened.
        _ = Task.Run(async () =>
        {
            // Only a focus change lands mid-tab-switch; a title change already waited out the
            // debounce, so its title is settled and delaying again would only add lag.
            if (kind == EventKind.Focus)
                await Task.Delay(TitleSettleMs).ConfigureAwait(false);

            var (_, title) = WindowInfo.Read(hwnd);
            var url = BrowserUrlReader.TryRead(hwnd);
            if (IsRepeat(processName, title))
                return;

            EventCaptured?.Invoke(new TrackedEvent
            {
                Timestamp = timestamp,
                Kind = kind,
                ProcessName = processName,
                WindowTitle = title,
                Url = url,
            });
        });
    }

    /// <summary>
    /// True when this is the same window state the last recorded event already described.
    /// Browser events settle on the thread pool, so this is reached from more than one thread.
    /// </summary>
    private bool IsRepeat(string processName, string windowTitle)
    {
        lock (_lastSeenGate)
        {
            if (processName == _lastProcess && windowTitle == _lastTitle)
                return true;

            _lastProcess = processName;
            _lastTitle = windowTitle;
            return false;
        }
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
