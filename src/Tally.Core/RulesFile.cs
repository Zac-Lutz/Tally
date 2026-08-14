using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>Loads the user-editable classification rules from JSON (comments allowed).</summary>
public static partial class RulesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The file is hand-editable, so keep written values as typed: the default encoder would turn a
    // regex's + and & into + / &. It's a local file, never HTML.
    private static readonly JsonSerializerOptions LiteralOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record RulesDocument(List<ClassificationRule> Rules);

    public static IReadOnlyList<ClassificationRule> Load(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<RulesDocument>(json, JsonOptions);
        return document?.Rules ?? [];
    }

    public static void WriteDefault(string path) => File.WriteAllText(path, DefaultRulesJson);

    /// <summary>
    /// Adds a rule to the rules file, leaving everything else — comments included — exactly as it
    /// was. Creates the file from the defaults first if it's missing.
    /// </summary>
    public static void AddRule(string path, ClassificationRule rule)
    {
        var json = File.Exists(path) ? File.ReadAllText(path) : DefaultRulesJson;
        File.WriteAllText(path, WithRule(json, rule));
    }

    /// <summary>
    /// The rules document with one rule added to its <c>rules</c> array, as a text edit — so
    /// comments, spacing, and every other rule survive untouched.
    /// <para>
    /// Position is earned by specificity, because the first matching rule wins: a rule with a title
    /// pattern goes <b>first</b> (it names one window, so it should beat anything generic), while an
    /// app-only rule goes <b>last</b> (it covers every window of an app, so it must not shadow the
    /// specific rules already there).
    /// </para>
    /// </summary>
    public static string WithRule(string json, ClassificationRule rule)
    {
        if (!TryFindRulesArray(json, out var open, out var close))
            throw new InvalidOperationException("The rules file has no \"rules\": [ ... ] array to add to.");

        var literal = RuleLiteral(rule);
        if (rule.TitlePattern is not null)
            return json.Insert(open + 1, $"\n    {literal},");

        // Appending: land right after the last rule, not after the array's closing indentation,
        // so the separating comma stays on that rule's line instead of stranded on its own.
        var last = LastMeaningfulIndex(json, open + 1, close);
        return last < 0
            ? json.Insert(close, $"\n    {literal}\n  ")
            : json.Insert(last + 1, $"{(json[last] == ',' ? "" : ",")}\n    {literal}");
    }

    private static string RuleLiteral(ClassificationRule rule)
    {
        List<string> parts = [$"\"id\": {Str(rule.Id)}"];
        if (rule.ProcessPattern is { } process)
            parts.Add($"\"processPattern\": {Str(process)}");
        if (rule.TitlePattern is { } title)
            parts.Add($"\"titlePattern\": {Str(title)}");
        parts.Add($"\"category\": {Str(rule.Category)}");
        if (rule.Client is { } client)
            parts.Add($"\"client\": {Str(client)}");
        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string Str(string value) => JsonSerializer.Serialize(value, LiteralOptions);

    // Locates the rules array's brackets. Scans with the same rules the reader uses — strings and
    // comments are skipped — so a "]" inside a title regex or a comment can't be mistaken for the end.
    private static bool TryFindRulesArray(string json, out int open, out int close)
    {
        open = close = -1;
        if (RulesArrayStart().Match(json) is not { Success: true } start)
            return false;

        open = start.Index + start.Length - 1;   // the '[' itself
        var depth = 0;
        for (var i = open; i < json.Length; i++)
        {
            switch (json[i])
            {
                case '"':
                    i = SkipString(json, i);
                    break;
                case '/' when i + 1 < json.Length && (json[i + 1] == '/' || json[i + 1] == '*'):
                    i = SkipComment(json, i);
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    if (--depth == 0)
                    {
                        close = i;
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    // The index of the last character inside [start, end) that isn't whitespace or part of a comment.
    private static int LastMeaningfulIndex(string json, int start, int end)
    {
        var last = -1;
        for (var i = start; i < end; i++)
        {
            if (json[i] == '"')
            {
                i = Math.Min(SkipString(json, i), end - 1);
                last = i;
            }
            else if (json[i] == '/' && i + 1 < end && (json[i + 1] == '/' || json[i + 1] == '*'))
            {
                i = SkipComment(json, i);
            }
            else if (!char.IsWhiteSpace(json[i]))
            {
                last = i;
            }
        }

        return last;
    }

    /// <summary>Index of the string's closing quote, given the index of its opening one.</summary>
    private static int SkipString(string json, int quote)
    {
        for (var i = quote + 1; i < json.Length; i++)
        {
            if (json[i] == '\\')
                i++;
            else if (json[i] == '"')
                return i;
        }

        return json.Length - 1;
    }

    /// <summary>Index of the comment's last character, given the index of its leading slash.</summary>
    private static int SkipComment(string json, int slash)
    {
        if (json[slash + 1] == '/')
        {
            var newline = json.IndexOf('\n', slash);
            return newline < 0 ? json.Length - 1 : newline;
        }

        var terminator = json.IndexOf("*/", slash + 2, StringComparison.Ordinal);
        return terminator < 0 ? json.Length - 1 : terminator + 1;
    }

    [GeneratedRegex("\"rules\"\\s*:\\s*\\[")]
    private static partial Regex RulesArrayStart();

    public const string DefaultRulesJson =
        """
        {
          // Ordered, first match wins. processPattern / titlePattern are case-insensitive regexes.
          // Named groups (?<ticket>...) and (?<client>...) in titlePattern extract those fields.
          "rules": [
            { "id": "halo-ticket", "titlePattern": "Ticket\\s*#?(?<ticket>\\d{3,})", "category": "Halo" },
            // HaloPSA's web app titles itself with unbranded breadcrumbs — "Tickets > Management >
            // <view>", ending in the ticket number when one is open — so the module names anchor
            // the match, and a trailing number is captured as the ticket.
            { "id": "halo-ticket-tab", "titlePattern": "^Tickets\\s*>.*>\\s*(?<ticket>\\d{3,})", "category": "Halo" },
            { "id": "halo-tab", "titlePattern": "^(Tickets|Clients|Users|Sites|Assets|Opportunities|Projects|Contracts|Suppliers|Invoices|Quotations|Reports|Configuration|Knowledge Base)\\s*>", "category": "Halo" },
            { "id": "halo", "titlePattern": "Halo\\s?PSA", "category": "Halo" },
            // IT Glue pages title themselves "<page> — IT Glue" (em-dash in the web app; hyphen and
            // en-dash allowed too), or lead with the product name.
            { "id": "itglue", "titlePattern": "(^|[-\\u2013\\u2014]\\s*)IT Glue\\b", "category": "IT Glue" },
            { "id": "screenconnect-client", "titlePattern": "^(?<client>.+?)\\s+[-\\u2013\\u2014].*(ScreenConnect|ConnectWise Control)", "category": "ScreenConnect" },
            { "id": "screenconnect", "titlePattern": "ScreenConnect|ConnectWise Control", "category": "ScreenConnect" },
            // Outlook wherever it's read: the desktop app (olk = new Outlook, outlook = classic) by
            // process, and OWA in a browser tab by its "Mail - <name> - Outlook" title shape. The
            // leading dash keeps a page merely mentioning Outlook from being claimed.
            { "id": "outlook-app", "processPattern": "^(olk|outlook)$", "category": "Outlook" },
            { "id": "owa", "titlePattern": "(^|[-\\u2013\\u2014]\\s*)Outlook\\b", "category": "Outlook" },
            // Teams window titles carry the focused chat/channel: "Chat | <name> | Microsoft Teams".
            // Capture that name as the subject so the rollup separates each conversation. Filed as
            // "Teams - Chat" so it reads apart from "Teams - Call" (which calls carry) on a
            // timesheet; a Teams window with no conversation in its title stays plain "Teams".
            { "id": "teams-chat", "processPattern": "^(ms-teams|msteams|Teams)$", "titlePattern": "^(?:Chat \\| )?(?<subject>.+?)\\s*\\| Microsoft Teams", "category": "Teams - Chat" },
            { "id": "teams", "processPattern": "^(ms-teams|msteams|Teams)$", "category": "Teams" },
            // Discord titles the focused channel, DM or view: "#channel | Server - Discord",
            // "@someone - Discord", "Friends - Discord". Capturing it as the subject separates each
            // conversation on the rollup; a bare "Discord" falls through to the rule below.
            { "id": "discord-channel", "processPattern": "^Discord$", "titlePattern": "^(?<subject>.+?)\\s*-\\s*Discord$", "category": "Discord" },
            { "id": "discord", "processPattern": "^Discord$", "category": "Discord" },
            // RingCentral ships under several executable names (the app, the older phone client,
            // meetings), so this matches the prefix rather than one exact name.
            { "id": "ringcentral", "processPattern": "^RingCentral", "category": "RingCentral" },
            { "id": "terminal", "processPattern": "^(WindowsTerminal|wt|OpenConsole|conhost|powershell|pwsh|cmd)$", "category": "Development" },
            { "id": "vscode", "processPattern": "^Code$", "category": "Development" },
            { "id": "visual-studio", "processPattern": "^devenv$", "category": "Development" }
            // No catch-all browser rule: a tab that matches nothing lands in Unclassified, where
            // the triage tab can teach it a real rule — a visible gap beats time quietly filed
            // under a generic "Browsing".
          ]
        }
        """;
}
