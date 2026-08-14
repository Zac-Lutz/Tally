using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>
/// Minimal in-place editor for a JSON(-with-comments) object: replaces a top-level string
/// property's value, or inserts the property if absent — preserving the rest of the file,
/// including comments (so the app can rewrite one setting without clobbering the annotated
/// settings file).
/// </summary>
public static partial class JsonValueEditor
{
    public static string SetStringProperty(string json, string key, string value)
    {
        var literal = "\"" + Encode(value) + "\"";
        var existing = StringPropertyRegex(key);

        if (existing.IsMatch(json))
            return existing.Replace(json, m => m.Groups[1].Value + literal, 1);

        // Absent: insert as the first property (trailing comma is fine — the reader allows it).
        var open = json.IndexOf('{');
        return open < 0 ? json : json.Insert(open + 1, "\n  \"" + key + "\": " + literal + ",");
    }

    /// <summary>Sets (or inserts) a top-level array-of-strings property, e.g. ["17:30", "12:00"].</summary>
    public static string SetStringArrayProperty(string json, string key, IReadOnlyList<string> values)
    {
        var literal = "[" + string.Join(", ", values.Select(v => "\"" + Encode(v) + "\"")) + "]";
        var existing = ArrayPropertyRegex(key);

        if (existing.IsMatch(json))
            return existing.Replace(json, m => m.Groups[1].Value + literal, 1);

        var open = json.IndexOf('{');
        return open < 0 ? json : json.Insert(open + 1, "\n  \"" + key + "\": " + literal + ",");
    }

    /// <summary>Sets (or inserts) a top-level integer property, e.g. 90. Replaces an existing null too.</summary>
    public static string SetNumberProperty(string json, string key, long value)
    {
        var literal = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var existing = NumberPropertyRegex(key);

        if (existing.IsMatch(json))
            return existing.Replace(json, m => m.Groups[1].Value + literal, 1);

        var open = json.IndexOf('{');
        return open < 0 ? json : json.Insert(open + 1, "\n  \"" + key + "\": " + literal + ",");
    }

    private static string Encode(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static Regex StringPropertyRegex(string key)
        => new("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"");

    // No nested brackets in a string array, so a simple [...] match is enough.
    private static Regex ArrayPropertyRegex(string key)
        => new("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\\[[^\\[\\]]*\\]");

    // Also matches null, so a hand-nulled setting is replaced instead of duplicated.
    private static Regex NumberPropertyRegex(string key)
        => new("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)(?:-?\\d+(?:\\.\\d+)?|null)");
}
