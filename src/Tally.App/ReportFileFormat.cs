namespace Tally.App;

public enum ReportFileFormat
{
    Html,
    Markdown,
}

public static class ReportFileFormats
{
    /// <summary>Parses a format string; anything unrecognized (or empty) resolves to HTML.</summary>
    public static ReportFileFormat Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ReportFileFormat.Markdown,
        _ => ReportFileFormat.Html,
    };

    public static string Extension(this ReportFileFormat format)
        => format == ReportFileFormat.Markdown ? "md" : "html";
}
