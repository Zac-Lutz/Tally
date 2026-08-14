namespace Tally.Core;

/// <summary>
/// The Settings tab's current values, re-read from settings.json for every render so the tab
/// always shows what the file actually says. Times are "HH:mm"; RetentionDays 0 = keep forever.
/// </summary>
public sealed record SettingsPanelState(
    string StartHotkey,
    string StopHotkey,
    IReadOnlyList<string> AutoReportTimes,
    int RetentionDays);
