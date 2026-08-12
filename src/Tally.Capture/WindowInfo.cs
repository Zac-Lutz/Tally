using System.Diagnostics;
using System.Text;

namespace Tally.Capture;

internal static class WindowInfo
{
    public const string UnknownProcess = "unknown";

    public static (string ProcessName, string Title) Read(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        _ = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (ProcessNameFromPid(pid), buffer.ToString());
    }

    public static string ProcessNameFromPid(uint pid)
    {
        if (pid == 0)
            return UnknownProcess;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return UnknownProcess;   // process exited between the event and the lookup
        }
        catch (InvalidOperationException)
        {
            return UnknownProcess;
        }
    }
}
