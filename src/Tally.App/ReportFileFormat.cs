namespace Tally.App;

public enum ReportFileFormat
{
    Html,
    Markdown,
    Json,
}

public static class ReportFileFormats
{
    /// <summary>Parses a format string; anything unrecognized (or empty) resolves to HTML.</summary>
    public static ReportFileFormat Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ReportFileFormat.Markdown,
        "json" => ReportFileFormat.Json,
        _ => ReportFileFormat.Html,
    };

    public static string Extension(this ReportFileFormat format) => format switch
    {
        ReportFileFormat.Markdown => "md",
        ReportFileFormat.Json => "json",
        _ => "html",
    };
}
