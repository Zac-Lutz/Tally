using System.Globalization;
using Tally.Core;
using Velopack;

namespace Tally.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must be first: handles Velopack's install/update/uninstall hook invocations and exits
        // for them. Returns immediately for a normal launch or --report, so nothing else changes.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Disable())
            .Run();

        // One-shot headless mode: tally.exe --report [today|yesterday|yyyy-MM-dd] [html|md|json]
        // Bypasses the single-instance mutex — SQLite WAL supports a reader alongside the tray writer.
        if (args.Length > 0 && args[0].Equals("--report", StringComparison.OrdinalIgnoreCase))
            return RunReport(args.Length > 1 ? args[1] : "today", args.Length > 2 ? args[2] : null);

        // Standalone live dashboard (also usable as a desktop shortcut). Read-only, so it runs
        // alongside the tray recorder without the single-instance mutex.
        if (args.Length > 0 && args[0].Equals("--live", StringComparison.OrdinalIgnoreCase))
            return RunLive();

        using var mutex = new Mutex(initiallyOwned: true, @"Local\Tally_SingleInstance_5B2E9C4A", out var createdNew);
        if (!createdNew)
            return 0;   // already running in this session

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        return 0;
    }

    private static int RunLive()
    {
        try
        {
            TallyPaths.EnsureCreated();
            if (!File.Exists(TallyPaths.RulesPath))
                RulesFile.WriteDefault(TallyPaths.RulesPath);
            var settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);

            ApplicationConfiguration.Initialize();
            var window = new LiveWindow(
                TallyDbContext.BuildOptions(TallyPaths.DatabasePath), settings, settings.ResolveReportsDirectory())
            {
                HideOnClose = false,   // standalone: closing exits
            };
            Application.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("--live failed", ex);
            return 1;
        }
    }

    private static int RunReport(string dateArg, string? formatArg)
    {
        try
        {
            TallyPaths.EnsureCreated();
            if (!File.Exists(TallyPaths.RulesPath))
                RulesFile.WriteDefault(TallyPaths.RulesPath);
            var settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);

            var date = dateArg.ToLowerInvariant() switch
            {
                "today" => DateOnly.FromDateTime(DateTime.Now),
                "yesterday" => DateOnly.FromDateTime(DateTime.Now.AddDays(-1)),
                _ => DateOnly.ParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            };

            // An explicit CLI arg overrides the settings default.
            var format = formatArg is null ? settings.ResolveReportFormat() : ReportFileFormats.Parse(formatArg);

            // Safe to block: no message pump or synchronization context exists yet.
            var path = ReportGenerator
                .GenerateAsync(TallyDbContext.BuildOptions(TallyPaths.DatabasePath), date, settings.ResolveReportsDirectory(), format)
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
