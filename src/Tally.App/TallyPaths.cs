namespace Tally.App;

public static class TallyPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tally");

    public static string DatabasePath => Path.Combine(Root, "tally.db");
    public static string RulesPath => Path.Combine(Root, "rules.json");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string ReportsDirectory => Path.Combine(Root, "reports");
    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
