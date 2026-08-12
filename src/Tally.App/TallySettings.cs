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
    public string? AutoReportTime { get; init; } = "17:00";

    /// <summary>Open the report file when the timed report generates (a tray balloon always shows).</summary>
    public bool OpenReportOnAutoGenerate { get; init; }

    public TimeOnly? ParseAutoReportTime()
        => TimeOnly.TryParseExact(AutoReportTime, ["HH:mm", "H:mm"], out var time) ? time : null;

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
          "autoReportTime": "17:00",
          // Open the report automatically when the timed report generates (a tray balloon always shows).
          "openReportOnAutoGenerate": false
        }
        """;
}
