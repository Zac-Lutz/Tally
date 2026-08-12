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
        var encoded = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var existing = PropertyRegex(key);

        if (existing.IsMatch(json))
            return existing.Replace(json, m => m.Groups[1].Value + "\"" + encoded + "\"", 1);

        // Absent: insert as the first property (trailing comma is fine — the reader allows it).
        var open = json.IndexOf('{');
        return open < 0 ? json : json.Insert(open + 1, "\n  \"" + key + "\": \"" + encoded + "\",");
    }

    private static Regex PropertyRegex(string key)
        => new("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"");
}
