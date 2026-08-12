using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>
/// Collapses volatile browser-chrome noise so repeated views of the SAME tab share one key.
/// The tab count ("and 7 more pages") changes as tabs open/close, so without this the same page
/// fragments into many rollup rows. The trailing browser name is stable but stripped for a
/// cleaner display name.
/// </summary>
public static partial class TitleNormalizer
{
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var trimmed = MorePages().Replace(title, string.Empty);
        trimmed = BrowserTail().Replace(trimmed, string.Empty).Trim();
        return trimmed.Length == 0 ? title.Trim() : trimmed;
    }

    // " and 7 more pages" / " and 1 more tab" — the fragmenting part.
    [GeneratedRegex(@"\s+and \d+ more (?:page|tab)s?", RegexOptions.IgnoreCase)]
    private static partial Regex MorePages();

    // Trailing " - Microsoft Edge" / " - Google Chrome" / " - Mozilla Firefox" (Edge inserts a
    // zero-width space between the words). The dash may be a hyphen or en/em dash.
    [GeneratedRegex(@"\s*[-–—]\s*(?:Google Chrome|Microsoft[\s​]+Edge|Mozilla Firefox)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserTail();
}
