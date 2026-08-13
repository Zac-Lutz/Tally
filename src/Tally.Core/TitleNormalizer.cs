using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>
/// Collapses volatile window-title noise so repeated views of the SAME thing share one key.
/// The tab count ("and 7 more pages") changes as tabs open/close and an editor's unsaved marker
/// flips as you type and save, so without this one page or one file fragments into several rollup
/// rows. The trailing browser name is stable but stripped for a cleaner display name.
/// </summary>
public static partial class TitleNormalizer
{
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var trimmed = UnsavedMarker().Replace(title, string.Empty);
        trimmed = MorePages().Replace(trimmed, string.Empty);
        trimmed = BrowserTail().Replace(trimmed, string.Empty).Trim();
        return trimmed.Length == 0 ? title.Trim() : trimmed;
    }

    // The unsaved-changes marker editors prefix to the title — "*" (Notepad++, Notepad, Sublime)
    // or a bullet (VS Code). It flips as you type and save, so the same file would otherwise be
    // two rollup rows: one "dirty" and one saved.
    [GeneratedRegex(@"^\s*[*●•]\s*")]
    private static partial Regex UnsavedMarker();

    // " and 7 more pages" / " and 1 more tab" — the fragmenting part.
    [GeneratedRegex(@"\s+and \d+ more (?:page|tab)s?", RegexOptions.IgnoreCase)]
    private static partial Regex MorePages();

    // Trailing " - Microsoft Edge" / " - Google Chrome" / " - Mozilla Firefox" (Edge inserts a
    // zero-width space between the words). The dash may be a hyphen or en/em dash.
    [GeneratedRegex(@"\s*[-–—]\s*(?:Google Chrome|Microsoft[\s​]+Edge|Mozilla Firefox)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserTail();
}
