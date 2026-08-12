using System.Text.Json;

namespace Tally.Core;

/// <summary>Loads the user-editable classification rules from JSON (comments allowed).</summary>
public static class RulesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record RulesDocument(List<ClassificationRule> Rules);

    public static IReadOnlyList<ClassificationRule> Load(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<RulesDocument>(json, JsonOptions);
        return document?.Rules ?? [];
    }

    public static void WriteDefault(string path) => File.WriteAllText(path, DefaultRulesJson);

    public const string DefaultRulesJson =
        """
        {
          // Ordered, first match wins. processPattern / titlePattern are case-insensitive regexes.
          // Named groups (?<ticket>...) and (?<client>...) in titlePattern extract those fields.
          "rules": [
            { "id": "halo-ticket", "titlePattern": "Ticket\\s*#?(?<ticket>\\d{3,})", "category": "HaloPSA" },
            { "id": "halo", "titlePattern": "Halo\\s?PSA", "category": "HaloPSA" },
            { "id": "screenconnect-client", "titlePattern": "^(?<client>.+?)\\s+[-\\u2013\\u2014].*(ScreenConnect|ConnectWise Control)", "category": "Remote Support" },
            { "id": "screenconnect", "titlePattern": "ScreenConnect|ConnectWise Control", "category": "Remote Support" },
            { "id": "owa", "titlePattern": "Outlook", "category": "Email" },
            // Teams window titles carry the focused chat/channel: "Chat | <name> | Microsoft Teams".
            // Capture that name as the subject so the rollup separates each conversation.
            { "id": "teams-chat", "processPattern": "^(ms-teams|msteams|Teams)$", "titlePattern": "^(?:Chat \\| )?(?<subject>.+?)\\s*\\| Microsoft Teams", "category": "Teams" },
            { "id": "teams", "processPattern": "^(ms-teams|msteams|Teams)$", "category": "Teams" },
            { "id": "terminal", "processPattern": "^(WindowsTerminal|wt|OpenConsole|conhost|powershell|pwsh|cmd)$", "category": "Development" },
            { "id": "vscode", "processPattern": "^Code$", "category": "Development" },
            { "id": "visual-studio", "processPattern": "^devenv$", "category": "Development" },
            { "id": "browser", "processPattern": "^(chrome|msedge|firefox)$", "category": "Browsing" }
          ]
        }
        """;
}
