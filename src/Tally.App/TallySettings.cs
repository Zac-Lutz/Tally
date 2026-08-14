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

    /// <summary>Legacy single auto-report time; superseded by <see cref="AutoReportTimes"/> when present.</summary>
    public string? AutoReportTime { get; init; } = "17:30";

    /// <summary>Local times (HH:mm) to auto-generate the report. Several = multiple snapshots; empty disables.</summary>
    public string[]? AutoReportTimes { get; init; }

    /// <summary>Open the report file when the timed report generates (a tray balloon always shows).</summary>
    public bool OpenReportOnAutoGenerate { get; init; }

    /// <summary>Directory reports are written to. Null uses %USERPROFILE%\.tally\reports.</summary>
    public string? ReportsDirectory { get; init; }

    /// <summary>Report file format: "html" (default), "markdown", or "json".</summary>
    public string ReportFormat { get; init; } = "html";

    /// <summary>Start Tally automatically at login (per-user HKCU Run entry).</summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>Days of raw activity history to keep; older whole days are deleted daily. 0 = keep forever; null = default.</summary>
    public int? EventRetentionDays { get; init; }

    /// <summary>Global hotkey to start a manual timer, e.g. "Ctrl+Alt+T".</summary>
    public string TimerStartHotkey { get; init; } = "Ctrl+Alt+T";

    /// <summary>Global hotkey to stop the running manual timer, e.g. "Ctrl+Alt+S".</summary>
    public string TimerStopHotkey { get; init; } = "Ctrl+Alt+S";

    /// <summary>
    /// The distinct, sorted auto-report times. Uses <see cref="AutoReportTimes"/> when set (even
    /// empty, to allow disabling); otherwise falls back to the legacy single <see cref="AutoReportTime"/>.
    /// Unparseable entries are skipped.
    /// </summary>
    public IReadOnlyList<TimeOnly> ResolveAutoReportTimes()
    {
        var raw = AutoReportTimes is not null
            ? AutoReportTimes.AsEnumerable()
            : AutoReportTime is { } single ? [single] : [];

        var times = new SortedSet<TimeOnly>();
        foreach (var s in raw)
            if (s is not null && TimeOnly.TryParseExact(s.Trim(), ["HH:mm", "H:mm"], out var t))
                times.Add(t);
        return times.ToList();
    }

    public string ResolveReportsDirectory()
        => string.IsNullOrWhiteSpace(ReportsDirectory)
            ? TallyPaths.ReportsDirectory
            : Environment.ExpandEnvironmentVariables(ReportsDirectory);

    /// <summary>Resolves the format string; anything unrecognized falls back to HTML.</summary>
    public ReportFileFormat ResolveReportFormat() => ReportFileFormats.Parse(ReportFormat);

    /// <summary>Raw-event retention in days; a missing setting gets the default. 0 = keep forever.</summary>
    public int ResolveEventRetentionDays() => EventRetentionDays ?? DefaultEventRetentionDays;

    public const int DefaultEventRetentionDays = 90;

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
          // Local times (HH:mm) to auto-generate the report. Add several for multiple snapshots
          // per day; empty [] disables. Configure these under Settings in the app. If tally starts
          // after a time, it catches up once shortly after startup.
          "autoReportTimes": ["17:30"],
          // Open the report automatically when the timed report generates (a tray balloon always shows).
          "openReportOnAutoGenerate": false,
          // Where reports are written; environment variables (%USERPROFILE%) are expanded.
          // null = %USERPROFILE%\.tally\reports
          "reportsDirectory": null,
          // Report file format: "html" (default), "markdown", or "json".
          "reportFormat": "html",
          // How many days of raw activity history to keep. Whole days older than this are deleted
          // once a day so the database stops growing (saved report files are never touched).
          // 0 = keep everything forever; values 1-6 are treated as 7.
          "eventRetentionDays": 90,
          // Start Tally automatically at login.
          "autoStart": true,
          // Global hotkeys for manual timers. Combine Ctrl/Alt/Shift/Win + a letter/F-key.
          "timerStartHotkey": "Ctrl+Alt+T",
          "timerStopHotkey": "Ctrl+Alt+S"
        }
        """;
}
