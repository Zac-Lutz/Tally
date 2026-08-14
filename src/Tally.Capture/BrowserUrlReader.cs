using System.Windows.Automation;
using Tally.Core;

namespace Tally.Capture;

/// <summary>
/// Reads a browser window's address bar through UI Automation — no browser extension, nothing to
/// install, everything stays local. The address bar is the first Edit control in the window's
/// automation tree (toolbars come before page content in tree order for Chrome, Edge, and
/// Firefox alike). What it holds passes through <see cref="UrlSanitizer"/>, so half-typed search
/// text and internal pages come back null, and query strings never get stored.
/// <para>
/// Best-effort by design: any UIA hiccup (window gone, tree busy, unsupported browser build)
/// yields null and the event simply carries no URL — exactly the pre-capture behavior.
/// </para>
/// </summary>
public static class BrowserUrlReader
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
    };

    private static readonly Condition AddressBarCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    public static bool IsBrowser(string processName) => Browsers.Contains(processName);

    /// <summary>The sanitized page the window's address bar shows, or null.</summary>
    public static string? TryRead(IntPtr hwnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            var addressBar = window?.FindFirst(TreeScope.Descendants, AddressBarCondition);
            if (addressBar is null
                || !addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                || pattern is not ValuePattern value)
            {
                return null;
            }

            return UrlSanitizer.Sanitize(value.Current.Value);
        }
        catch
        {
            return null;
        }
    }
}
