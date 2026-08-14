using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>A user-defined category: its display name and hex colour, e.g. "#8b5cf6".</summary>
public sealed record CategoryDefinition(string Name, string Color);

/// <summary>
/// The user's own categories, in %USERPROFILE%\.tally\categories.json. Unlike rules.json this
/// file is app-owned — the Categories tab is its editor — so it round-trips through the
/// serializer rather than comment-preserving text edits.
/// </summary>
public static class CategoriesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private sealed record CategoriesDocument(List<CategoryDefinition> Categories);

    public static IReadOnlyList<CategoryDefinition> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        var document = JsonSerializer.Deserialize<CategoriesDocument>(File.ReadAllText(path), JsonOptions);
        return document?.Categories ?? [];
    }

    /// <summary>Adds the category, or recolours it if a same-named one (any casing) exists.</summary>
    public static void Upsert(string path, string name, string colorHex)
    {
        var categories = Load(path).ToList();
        var existing = categories.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            categories[existing] = categories[existing] with { Color = colorHex };
        else
            categories.Add(new CategoryDefinition(name, colorHex));
        Save(path, categories);
    }

    /// <summary>
    /// Renames a category entry, keeping its colour. Renaming onto an existing name merges into
    /// it (the target keeps its own colour). Returns false when no entry carried the old name —
    /// the rules may still be renamed by the caller; there's just no colour entry to move.
    /// </summary>
    public static bool Rename(string path, string oldName, string newName)
    {
        var categories = Load(path).ToList();
        var source = categories.FindIndex(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (source < 0)
            return false;

        var target = categories.FindIndex(c => string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (target >= 0 && target != source)
            categories.RemoveAt(source);
        else
            categories[source] = categories[source] with { Name = newName };
        Save(path, categories);
        return true;
    }

    /// <summary>Removes the entry (any casing). Returns false when there was none.</summary>
    public static bool Remove(string path, string name)
    {
        var categories = Load(path).ToList();
        var removed = categories.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return false;

        Save(path, categories);
        return true;
    }

    private static void Save(string path, List<CategoryDefinition> categories)
        => File.WriteAllText(path, JsonSerializer.Serialize(new CategoriesDocument(categories), JsonOptions));
}

/// <summary>
/// Category → colour lookup for rendering: the user's definitions first, then the shipped hues,
/// then gray. Names match case-insensitively, so "halo" recolours "Halo".
/// </summary>
public sealed partial class CategoryPalette
{
    public static readonly CategoryPalette Empty = new([]);

    private readonly Dictionary<string, string> _rgbByName;

    public CategoryPalette(IEnumerable<CategoryDefinition> definitions)
    {
        _rgbByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            // An unparseable colour or blank name is skipped, not fatal — the category just
            // falls back to its default hue.
            if (!string.IsNullOrWhiteSpace(definition.Name) && HexToRgb(definition.Color) is { } rgb)
                _rgbByName[definition.Name.Trim()] = rgb;
        }
    }

    /// <summary>The user's "r,g,b" for this category, or null to fall back to the defaults.</summary>
    public string? CustomRgb(string category) => _rgbByName.GetValueOrDefault(category);

    /// <summary>"#3b82f6" (leading # optional) → "59,130,246"; null when it isn't a hex colour.</summary>
    public static string? HexToRgb(string? hex)
    {
        if (hex is null || HexColor().Match(hex.Trim()) is not { Success: true } match)
            return null;

        var value = match.Groups[1].Value;
        return string.Join(",",
            Convert.ToInt32(value[..2], 16),
            Convert.ToInt32(value[2..4], 16),
            Convert.ToInt32(value[4..], 16));
    }

    /// <summary>"59,130,246" → "#3b82f6" — for prefilling a colour input with the effective hue.</summary>
    public static string RgbToHex(string rgb)
    {
        var parts = rgb.Split(',');
        return $"#{int.Parse(parts[0]):x2}{int.Parse(parts[1]):x2}{int.Parse(parts[2]):x2}";
    }

    [GeneratedRegex("^#?([0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();
}
