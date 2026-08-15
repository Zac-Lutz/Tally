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
    // Set from settings at startup; empty until then, so a caller that never configures profiles
    // simply keeps the behaviour this had before profiles existed.
    private static Regex? _profileTail;

    /// <summary>
    /// Teaches the normalizer which browser profile names to drop, e.g. <c>["Work"]</c>. Chromium
    /// titles a window "&lt;page&gt; - &lt;profile&gt; - Microsoft Edge" once a second profile
    /// exists, so without this every captured browser title trails the profile name. The names are
    /// configured rather than inferred because the segment before the browser is only a profile if
    /// it happens to be one — stripping it blind would take the end off "Ticket 495308 - Install
    /// Teams". Matching therefore requires the browser name to follow, so the same words appearing
    /// anywhere else in a title are left alone.
    /// </summary>
    public static void ConfigureBrowserProfiles(IEnumerable<string>? profiles)
    {
        var names = (profiles ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Regex.Escape(p.Trim()))
            .ToList();

        _profileTail = names.Count == 0
            ? null
            : new Regex(
                $@"\s*[-–—]\s*(?:{string.Join('|', names)})\s*(?={BrowserNames})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var trimmed = UnsavedMarker().Replace(title, string.Empty);
        trimmed = MorePages().Replace(trimmed, string.Empty);
        if (_profileTail is { } profile)
            trimmed = profile.Replace(trimmed, string.Empty);
        trimmed = BrowserTail().Replace(trimmed, string.Empty).Trim();
        return trimmed.Length == 0 ? title.Trim() : trimmed;
    }

    // Shared by the profile lookahead and the browser tail, so the two can never disagree about
    // what counts as a browser.
    private const string BrowserNames =
        @"[-–—]\s*(?:Google Chrome|Microsoft[\s​]+Edge|Mozilla Firefox)\s*$";

    // A status glyph a window prefixes to its title: the unsaved-changes marker of an editor —
    // "*" (Notepad++, Notepad, Sublime) or a bullet (VS Code) — and the animated spinner a
    // console tool cycles while it works. Both flip while the window sits on the same thing, so
    // one file, or one long-running task, would otherwise fragment into a row per frame. Seen in
    // real capture: "◐ Resume Tally…", "◑ Resume Tally…" and "✳ Resume Tally…" counted as three
    // separate activities across fourteen minutes of one job.
    [GeneratedRegex(@"^\s*[*●•◐◑◒◓◔◕◖◗✳✴✶✷✸✹✺⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]+\s*")]
    private static partial Regex UnsavedMarker();

    // " and 7 more pages" / " and 1 more tab" — the fragmenting part.
    [GeneratedRegex(@"\s+and \d+ more (?:page|tab)s?", RegexOptions.IgnoreCase)]
    private static partial Regex MorePages();

    // Trailing " - Microsoft Edge" / " - Google Chrome" / " - Mozilla Firefox" (Edge inserts a
    // zero-width space between the words). The dash may be a hyphen or en/em dash.
    [GeneratedRegex(@"\s*[-–—]\s*(?:Google Chrome|Microsoft[\s​]+Edge|Mozilla Firefox)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserTail();
}
