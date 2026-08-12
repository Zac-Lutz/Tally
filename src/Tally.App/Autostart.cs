using Microsoft.Win32;

namespace Tally.App;

/// <summary>
/// Registers/removes the per-user autostart entry (HKCU ...\Run\Tally) pointing at the running
/// executable. The app owns this so autostart works wherever it was installed — dev publish or
/// the Velopack installer — and self-heals if the path changes after an update.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tally";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else if (!enabled)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update autostart registration", ex);
        }
    }

    public static void Disable() => Apply(false);
}
