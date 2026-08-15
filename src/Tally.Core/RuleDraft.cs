using System.Text;

namespace Tally.Core;

/// <summary>How much of the day a rule drafted from one activity should cover.</summary>
public enum RuleMatch
{
    /// <summary>Every window of that app.</summary>
    App,

    /// <summary>Only that app showing that window title.</summary>
    Window,
}

/// <summary>
/// Turns one unclassified activity into a classification rule, ready to append to rules.json.
/// The patterns are literal — every regex metacharacter is escaped — because a rule the app writes
/// should match exactly what the user pointed at, and stay readable if they hand-edit it later.
/// </summary>
public static class RuleDraft
{
    public static ClassificationRule Create(
        string processName,
        string title,
        RuleMatch match,
        string category,
        IEnumerable<string>? existingIds = null,
        bool exclude = false)
    {
        var trimmedCategory = category.Trim();
        if (trimmedCategory.Length == 0)
            throw new ArgumentException("A rule needs a category.", nameof(category));

        var process = processName.Trim() is { Length: > 0 } p ? $"^{EscapeLiteral(p)}$" : null;
        var activity = TitleNormalizer.Normalize(title).Trim();

        // No process name to key on (rare) — fall back to matching the title, whatever was asked for.
        var byWindow = match == RuleMatch.Window || process is null;
        var titlePattern = byWindow && activity.Length > 0 ? EscapeLiteral(activity) : null;
        if (process is null && titlePattern is null)
            throw new ArgumentException("A rule needs an app or a window title to match.", nameof(processName));

        return new ClassificationRule
        {
            Id = UniqueId(trimmedCategory, titlePattern is not null ? activity : processName, existingIds),
            ProcessPattern = process,
            TitlePattern = titlePattern,
            Category = trimmedCategory,
            Exclude = exclude,
        };
    }

    /// <summary>
    /// A readable id for a hand-written rule ("halo", "halo-2", …), unique among
    /// <paramref name="existingIds"/>. Hand-written rules carry regex patterns, so unlike a
    /// drafted rule there's no literal activity to name the id after — the category is the name.
    /// </summary>
    public static string ManualId(string category, IEnumerable<string>? existingIds = null)
        => UniqueId(category.Trim(), string.Empty, existingIds);

    // Escapes the regex metacharacters only. Regex.Escape also escapes every space (as "\ ", for
    // IgnorePatternWhitespace mode, which the Classifier doesn't use) — correct but unreadable in a
    // file the user is invited to edit.
    private const string Metacharacters = @"\^$.|?*+()[]{}#";

    private static string EscapeLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (Metacharacters.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    // A readable, stable id ("email-inbox-outlook"), suffixed -2, -3… if that name is taken.
    private static string UniqueId(string category, string basis, IEnumerable<string>? existingIds)
    {
        var slug = Join(Slug(category), Slug(basis));
        if (slug.Length == 0)
            slug = "rule";

        var taken = new HashSet<string>(existingIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug))
            return slug;

        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static string Join(string a, string b)
        => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}-{b}";

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (sb.Length >= 40)
                break;
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        return sb.ToString().Trim('-');
    }
}
