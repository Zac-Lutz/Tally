using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>
/// Counts keyboard and mouse input as an activity-intensity signal and emits an
/// <see cref="ActivitySample"/> once per flush interval. Install on a thread with a message
/// pump (the WinForms UI thread); the low-level hook callbacks are delivered on that thread.
///
/// PRIVACY: this is a COUNTER, not a keylogger. The keyboard callback increments on key-down
/// messages but deliberately never reads <c>KBDLLHOOKSTRUCT.vkCode</c> from <c>lParam</c>, so
/// which key was pressed is never observed, stored, or recoverable. The mouse callback counts
/// button/wheel messages and ignores movement.
/// </summary>
public sealed class ActivityWatcher : IDisposable
{
    public event Action<ActivitySample>? SampleReady;

    // Rooted in fields so the GC never collects the delegates the native hooks call back into.
    private readonly NativeMethods.LowLevelHookProc _keyboardProc;
    private readonly NativeMethods.LowLevelHookProc _mouseProc;
    private readonly System.Windows.Forms.Timer _flushTimer;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    // Hook callbacks and the flush timer all run on the install thread (UI thread), so plain
    // fields need no locking.
    private int _keystrokes;
    private int _mouseClicks;

    public ActivityWatcher(TimeSpan? flushInterval = null)
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _flushTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)(flushInterval ?? TimeSpan.FromSeconds(60)).TotalMilliseconds,
        };
        _flushTimer.Tick += (_, _) => Flush();
    }

    public void Start()
    {
        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
        _flushTimer.Start();
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
                _keystrokes++;   // count only — lParam (the key identity) is never read
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            switch ((int)wParam)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_MBUTTONDOWN:
                case NativeMethods.WM_XBUTTONDOWN:
                case NativeMethods.WM_MOUSEWHEEL:
                    _mouseClicks++;
                    break;
                // WM_MOUSEMOVE and all other messages are deliberately ignored — the callback
                // stays trivial so the low-level mouse hook adds no perceptible input latency.
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void Flush()
    {
        var keys = _keystrokes;
        var clicks = _mouseClicks;
        // Reset every interval regardless of whether a subscriber persists the sample, so a
        // paused stretch never accumulates into one spike on resume.
        _keystrokes = 0;
        _mouseClicks = 0;

        if (keys > 0 || clicks > 0)
            SampleReady?.Invoke(new ActivitySample { Timestamp = DateTimeOffset.Now, Keystrokes = keys, MouseClicks = clicks });
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        if (_keyboardHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }
}
