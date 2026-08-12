using System.Globalization;
using Tally.Core;

namespace Tally.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One-shot headless mode: tally.exe --report [today|yesterday|yyyy-MM-dd]
        // Bypasses the single-instance mutex — SQLite WAL supports a reader alongside the tray writer.
        if (args.Length > 0 && args[0].Equals("--report", StringComparison.OrdinalIgnoreCase))
            return RunReport(args.Length > 1 ? args[1] : "today");

        using var mutex = new Mutex(initiallyOwned: true, @"Local\Tally_SingleInstance_5B2E9C4A", out var createdNew);
        if (!createdNew)
            return 0;   // already running in this session

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        return 0;
    }

    private static int RunReport(string dateArg)
    {
        try
        {
            TallyPaths.EnsureCreated();
            if (!File.Exists(TallyPaths.RulesPath))
                RulesFile.WriteDefault(TallyPaths.RulesPath);

            var date = dateArg.ToLowerInvariant() switch
            {
                "today" => DateOnly.FromDateTime(DateTime.Now),
                "yesterday" => DateOnly.FromDateTime(DateTime.Now.AddDays(-1)),
                _ => DateOnly.ParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            };

            // Safe to block: no message pump or synchronization context exists yet.
            var path = ReportGenerator
                .GenerateAsync(TallyDbContext.BuildOptions(TallyPaths.DatabasePath), date)
                .GetAwaiter()
                .GetResult();
            Log.Info($"Report generated via --report: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"--report {dateArg} failed", ex);
            return 1;
        }
    }
}
