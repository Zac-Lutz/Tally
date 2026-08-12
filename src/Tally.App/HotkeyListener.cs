using System.Runtime.InteropServices;

namespace Tally.App;

/// <summary>
/// Registers two global hotkeys (start / stop a manual timer) via a hidden message window and
/// invokes callbacks on WM_HOTKEY. Registration failures (e.g. a combo another app owns) are
/// logged, not fatal.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;   // don't refire while the keys are held
    private const int StartId = 1;
    private const int StopId = 2;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly MessageWindow _window;
    private readonly Action _onStart;
    private readonly Action _onStop;

    public HotkeyListener(string startHotkey, string stopHotkey, Action onStart, Action onStop)
    {
        _onStart = onStart;
        _onStop = onStop;
        _window = new MessageWindow(OnHotkey);
        Register(StartId, startHotkey);
        Register(StopId, stopHotkey);
    }

    /// <summary>Re-registers both hotkeys with new specs (used when reconfigured in the app).</summary>
    public void Rebind(string startHotkey, string stopHotkey)
    {
        UnregisterHotKey(_window.Handle, StartId);
        UnregisterHotKey(_window.Handle, StopId);
        Register(StartId, startHotkey);
        Register(StopId, stopHotkey);
    }

    private void Register(int id, string spec)
    {
        if (!HotkeySpec.TryParse(spec, out var modifiers, out var vk))
        {
            Log.Error($"Invalid timer hotkey '{spec}' — expected e.g. Ctrl+Alt+T");
            return;
        }

        if (!RegisterHotKey(_window.Handle, id, modifiers | ModNoRepeat, vk))
            Log.Error($"Could not register timer hotkey '{spec}' — it may already be in use by another app");
    }

    private void OnHotkey(int id)
    {
        try
        {
            if (id == StartId)
                _onStart();
            else if (id == StopId)
                _onStop();
        }
        catch (Exception ex)
        {
            Log.Error("Timer hotkey handler failed", ex);
        }
    }

    public void Dispose()
    {
        UnregisterHotKey(_window.Handle, StartId);
        UnregisterHotKey(_window.Handle, StopId);
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;

        public MessageWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());   // invisible; just a WM_HOTKEY sink
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
                _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}

/// <summary>Parses a "Ctrl+Alt+T"-style hotkey into RegisterHotKey modifier flags + a virtual-key code.</summary>
internal static class HotkeySpec
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;

    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows" or "meta": modifiers |= ModWin; break;
                default:
                    if (!TryParseKey(part, out vk))
                        return false;
                    break;
            }
        }

        return vk != 0 && modifiers != 0;   // require at least one modifier + one key
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            vk = char.ToUpperInvariant(key[0]);   // VK for A-Z/0-9 equals the ASCII code
            return true;
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (n - 1));   // VK_F1..VK_F24
            return true;
        }

        return false;
    }
}
