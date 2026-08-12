using Tally.Core;

namespace Tally.App;

/// <summary>Persists individual settings back to settings.json, preserving comments/formatting.</summary>
internal static class SettingsWriter
{
    public static void UpdateHotkeys(string path, string startSpec, string stopSpec)
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : TallySettings.DefaultJson;
            text = JsonValueEditor.SetStringProperty(text, "timerStartHotkey", startSpec);
            text = JsonValueEditor.SetStringProperty(text, "timerStopHotkey", stopSpec);
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save hotkey settings", ex);
        }
    }
}
