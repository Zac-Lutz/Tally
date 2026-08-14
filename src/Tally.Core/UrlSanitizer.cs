namespace Tally.Core;

/// <summary>
/// Normalizes what a browser address bar hands us into what Tally stores: <c>host/path</c> only —
/// no scheme, no query string, no fragment. Query strings are where the sensitive bits live
/// (search terms, tokens), and host+path is everything rules and tickets will ever need. Address
/// bars also show non-URLs (half-typed searches, internal pages); those come back null.
/// </summary>
public static class UrlSanitizer
{
    private const int MaxLength = 200;

    public static string? Sanitize(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' '))
            return null;

        // A bare "site.com/page" (Chrome hides the scheme) is a URL only if it looks like a host;
        // anything with an explicit scheme lets the parser decide.
        var hadScheme = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hadScheme ? text : "https://" + text, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme is not ("http" or "https"))
            return null;

        if (!hadScheme && !uri.Host.Contains('.'))
            return null;   // "settings" or "somesearchword" — not a page

        var path = uri.AbsolutePath.TrimEnd('/');
        var result = uri.Host.ToLowerInvariant() + path;
        return result.Length <= MaxLength ? result : result[..MaxLength];
    }
}
