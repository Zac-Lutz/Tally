using Tally.Core;

namespace Tally.App;

/// <summary>Persists settings back to settings.json, preserving comments/formatting.</summary>
internal static class SettingsWriter
{
    public static void UpdateSettings(string path, string startHotkey, string stopHotkey, IReadOnlyList<string> autoReportTimes, int eventRetentionDays)
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : TallySettings.DefaultJson;
            text = JsonValueEditor.SetStringProperty(text, "timerStartHotkey", startHotkey);
            text = JsonValueEditor.SetStringProperty(text, "timerStopHotkey", stopHotkey);
            text = JsonValueEditor.SetStringArrayProperty(text, "autoReportTimes", autoReportTimes);
            text = JsonValueEditor.SetNumberProperty(text, "eventRetentionDays", eventRetentionDays);
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
