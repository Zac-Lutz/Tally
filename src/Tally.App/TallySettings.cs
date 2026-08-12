using System.Text.Json;

namespace Tally.App;

/// <summary>User settings, loaded once at startup from %USERPROFILE%\.tally\settings.json.</summary>
public sealed record TallySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Local time (HH:mm) to auto-generate the daily report. Null disables the timer.</summary>
    public string? AutoReportTime { get; init; } = "17:30";

    /// <summary>Open the report file when the timed report generates (a tray balloon always shows).</summary>
    public bool OpenReportOnAutoGenerate { get; init; }

    /// <summary>Directory reports are written to. Null uses %USERPROFILE%\.tally\reports.</summary>
    public string? ReportsDirectory { get; init; }

    /// <summary>Report file format: "html" (default) or "markdown".</summary>
    public string ReportFormat { get; init; } = "html";

    public TimeOnly? ParseAutoReportTime()
        => TimeOnly.TryParseExact(AutoReportTime, ["HH:mm", "H:mm"], out var time) ? time : null;

    public string ResolveReportsDirectory()
        => string.IsNullOrWhiteSpace(ReportsDirectory)
            ? TallyPaths.ReportsDirectory
            : Environment.ExpandEnvironmentVariables(ReportsDirectory);

    /// <summary>Resolves the format string; anything unrecognized falls back to HTML.</summary>
    public ReportFileFormat ResolveReportFormat() => ReportFileFormats.Parse(ReportFormat);

    public static TallySettings LoadOrCreate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultJson);
                return new TallySettings();
            }

            return JsonSerializer.Deserialize<TallySettings>(File.ReadAllText(path), JsonOptions)
                ?? new TallySettings();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings.json — using defaults", ex);
            return new TallySettings();
        }
    }

    public const string DefaultJson =
        """
        {
          // Local time (HH:mm) to auto-generate the daily report; set to null to disable.
          // If tally starts after this time, it catches up once shortly after startup.
          "autoReportTime": "17:30",
          // Open the report automatically when the timed report generates (a tray balloon always shows).
          "openReportOnAutoGenerate": false,
          // Where reports are written; environment variables (%USERPROFILE%) are expanded.
          // null = %USERPROFILE%\.tally\reports
          "reportsDirectory": null,
          // Report file format: "html" (default) or "markdown".
          "reportFormat": "html"
        }
        """;
}
