using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tally.Core;

/// <summary>
/// Where a newly written rule belongs among the ones already there. First match wins, so this is
/// the difference between a rule taking effect and a broader rule above it swallowing everything
/// it was meant to catch.
/// </summary>
public enum RulePlacement
{
    /// <summary>Names one particular window or page: goes first, above everything.</summary>
    Specific,

    /// <summary>Names a whole website: goes below the specific rules, above the app-only ones.</summary>
    Site,
}

/// <summary>Loads the user-editable classification rules from JSON (comments allowed).</summary>
public static partial class RulesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new TolerantExcludeScopeConverter() },
    };

    /// <summary>
    /// Reads <c>"excludeFrom"</c> as a scope, and anything it doesn't recognize as
    /// <see cref="ExcludeScope.None"/>. The file is hand-editable and a strict enum would throw on
    /// a typo — which, because a failed load reads as no rules at all, would cost the user every
    /// rule they have over one misspelled word.
    /// </summary>
    private sealed class TolerantExcludeScopeConverter : JsonConverter<ExcludeScope>
    {
        public override ExcludeScope Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<ExcludeScope>(reader.GetString(), ignoreCase: true, out var scope))
            {
                return scope;
            }

            reader.Skip();
            return ExcludeScope.None;
        }

        public override void Write(Utf8JsonWriter writer, ExcludeScope value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }

    // The file is hand-editable, so keep written values as typed: the default encoder would turn a
    // regex's + and & into + / &. It's a local file, never HTML.
    private static readonly JsonSerializerOptions LiteralOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record RulesDocument(List<RuleEntry> Rules);

    /// <summary>
    /// A rule as the file may spell it, kept separate from the model so that a file written before
    /// window and page patterns merged still loads. Either old key is read as the one pattern a
    /// rule now has.
    /// </summary>
    private sealed class RuleEntry
    {
        public required string Id { get; init; }
        public string? ProcessPattern { get; init; }
        public string? MatchPattern { get; init; }

        /// <summary>Older spelling of <see cref="MatchPattern"/>, when it only read the title.</summary>
        public string? TitlePattern { get; init; }

        /// <summary>Older spelling of <see cref="MatchPattern"/>, when it only read the page.</summary>
        public string? UrlPattern { get; init; }

        public required string Category { get; init; }
        public string? Client { get; init; }
        public ExcludeScope ExcludeFrom { get; init; }

        public ClassificationRule ToRule() => new()
        {
            Id = Id,
            ProcessPattern = ProcessPattern,
            MatchPattern = Pattern(),
            Category = Category,
            Client = Client,
            ExcludeFrom = ExcludeFrom,
        };

        // A rule carrying both old patterns used to need both to match. There is no way to say
        // that any more, so the two are joined as alternatives — the rule keeps matching everything
        // it used to and may now match a little more, which is the safe direction: the activity
        // stays classified rather than falling back to Uncategorized.
        private string? Pattern() => (MatchPattern, TitlePattern, UrlPattern) switch
        {
            ({ } m, _, _) => m,
            (null, { } t, { } u) => $"(?:{t})|(?:{u})",
            (null, { } t, null) => t,
            (null, null, { } u) => u,
            _ => null,
        };
    }

    public static IReadOnlyList<ClassificationRule> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<ClassificationRule> Parse(string json)
    {
        var document = JsonSerializer.Deserialize<RulesDocument>(json, JsonOptions);
        return document?.Rules.Select(e => e.ToRule()).ToList() ?? [];
    }

    public static void WriteDefault(string path) => File.WriteAllText(path, DefaultRulesJson);

    /// <summary>
    /// Adds a rule to the rules file, leaving everything else — comments included — exactly as it
    /// was. Creates the file from the defaults first if it's missing.
    /// </summary>
    public static void AddRule(string path, ClassificationRule rule, RulePlacement placement = RulePlacement.Specific)
    {
        var json = File.Exists(path) ? File.ReadAllText(path) : DefaultRulesJson;
        File.WriteAllText(path, WithRule(json, rule, placement));
    }

    /// <summary>
    /// The rules document with one rule added to its <c>rules</c> array, as a text edit — so
    /// comments, spacing, and every other rule survive untouched.
    /// <para>
    /// Position is earned by specificity, because the first matching rule wins, and there are three
    /// tiers: a rule naming one particular thing goes <b>first</b>, a rule naming a whole website
    /// goes <b>after the other match rules</b> (broader than a window, narrower than an app), and
    /// an app-only rule goes <b>last</b>, since it covers everything that app does and must not
    /// shadow the specific rules already there.
    /// <para>
    /// The middle tier is why a site rule isn't simply put on top: a rule for halo.lutz.us would
    /// otherwise outrank the rule that pulls the ticket number out of a Halo window, and the
    /// tickets would quietly stop being extracted. That is the one thing to preserve here.
    /// Which tier a rule belongs to used to be readable from its shape, when a title pattern and a
    /// page pattern were separate fields; now that they are one field, the caller has to say.
    /// </para>
    /// </para>
    /// </summary>
    public static string WithRule(string json, ClassificationRule rule, RulePlacement placement = RulePlacement.Specific)
    {
        if (!TryFindRulesArray(json, out var open, out var close))
            throw new InvalidOperationException("The rules file has no \"rules\": [ ... ] array to add to.");

        var literal = RuleLiteral(rule);
        if (rule.MatchPattern is not null && placement is RulePlacement.Specific)
            return json.Insert(open + 1, $"\n    {literal},");

        if (rule.MatchPattern is not null
            && placement is RulePlacement.Site
            && LastMatchRuleEnd(json, open, close) is { } afterMatches)
        {
            return json.Insert(afterMatches + 1, $"{(json[afterMatches] == ',' ? "" : ",")}\n    {literal}");
        }

        // Appending: land right after the last rule, not after the array's closing indentation,
        // so the separating comma stays on that rule's line instead of stranded on its own.
        var last = LastMeaningfulIndex(json, open + 1, close);
        return last < 0
            ? json.Insert(close, $"\n    {literal}\n  ")
            : json.Insert(last + 1, $"{(json[last] == ',' ? "" : ",")}\n    {literal}");
    }

    // The character index the last rule with a match pattern ends on, or null when there are none
    // (in which case a site rule simply appends like an app rule would). Read from the parsed rules
    // so "has a match pattern" means the same thing here as it does to the classifier.
    private static int? LastMatchRuleEnd(string json, int open, int close)
    {
        var spans = RuleSpans(json, open, close);
        var rules = Parse(json);
        if (spans.Count != rules.Count)
            return null;   // the file didn't parse the way it scanned; fall back to appending

        for (var i = rules.Count - 1; i >= 0; i--)
            if (rules[i].MatchPattern is not null)
                return spans[i].End;

        return null;
    }

    /// <summary>
    /// Refiles every rule under <paramref name="oldName"/> to <paramref name="newName"/> in the
    /// file, returning how many changed. Nothing is written when none matched.
    /// </summary>
    public static int RenameCategory(string path, string oldName, string newName)
    {
        var updated = WithCategoryRenamed(File.ReadAllText(path), oldName, newName, out var renamed);
        if (renamed > 0)
            File.WriteAllText(path, updated);
        return renamed;
    }

    /// <summary>
    /// The rules document with every rule filed under <paramref name="oldName"/> rewritten in
    /// place under <paramref name="newName"/> — positions, other rules, and comments untouched.
    /// </summary>
    public static string WithCategoryRenamed(string json, string oldName, string newName, out int renamed)
    {
        var rules = Parse(json);
        renamed = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            if (!string.Equals(rules[i].Category, oldName, StringComparison.Ordinal))
                continue;

            // Replacing keeps rule count and order, so the index still means the same rule on
            // the next pass even though character positions shifted.
            json = WithRuleReplacedAt(json, i, rules[i] with { Category = newName });
            renamed++;
        }

        return json;
    }

    /// <summary>Removes the rule at <paramref name="index"/> (array order) from the file.</summary>
    public static void RemoveRuleAt(string path, int index)
        => File.WriteAllText(path, WithoutRuleAt(File.ReadAllText(path), index));

    /// <summary>Rewrites the rule at <paramref name="index"/> (array order) in place in the file.</summary>
    public static void ReplaceRuleAt(string path, int index, ClassificationRule rule)
        => File.WriteAllText(path, WithRuleReplacedAt(File.ReadAllText(path), index, rule));

    /// <summary>
    /// The rules document with the rule at <paramref name="index"/> removed, as a text edit — every
    /// other rule and every comment stays exactly as written. A comment that described the removed
    /// rule is deliberately left behind: comments here often cover a group of rules, so guessing
    /// which ones belonged to this rule risks deleting someone's note about its neighbour.
    /// </summary>
    public static string WithoutRuleAt(string json, int index)
    {
        var (open, close, spans) = RequireRuleSpans(json, index);
        var (start, endExclusive) = (spans[index].Start, spans[index].End + 1);

        // The separating comma goes with the rule: the one right after it, or — for the last
        // rule — the one before. (No adjacent comma found leaves a trailing comma at most,
        // which the reader allows.)
        var after = endExclusive;
        while (after < close && (json[after] == ' ' || json[after] == '\t'))
            after++;
        if (after < close && json[after] == ',')
        {
            endExclusive = after + 1;
        }
        else
        {
            var before = start - 1;
            while (before > open && char.IsWhiteSpace(json[before]))
                before--;
            if (json[before] == ',')
                start = before;
        }

        var result = json.Remove(start, endExclusive - start);

        // If the rule owned its line, the splice leaves a line of pure indentation — drop it.
        var lineStart = start == 0 ? 0 : result.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = start < result.Length ? result.IndexOf('\n', start) : -1;
        var line = lineEnd < 0 ? result[lineStart..] : result[lineStart..lineEnd];
        if (line.All(c => c is ' ' or '\t'))
            result = result.Remove(lineStart, (lineEnd < 0 ? result.Length : lineEnd + 1) - lineStart);

        return result;
    }

    /// <summary>
    /// The rules document with the rule at <paramref name="index"/> rewritten in place — same
    /// position (order is first-match-wins, so an edit must not move a rule), every other rule and
    /// comment untouched. The rewritten rule takes the standard one-line shape; any custom
    /// formatting inside that one rule's braces is the price of the edit.
    /// </summary>
    public static string WithRuleReplacedAt(string json, int index, ClassificationRule rule)
    {
        var (_, _, spans) = RequireRuleSpans(json, index);
        var (start, end) = spans[index];
        return json.Remove(start, end - start + 1).Insert(start, RuleLiteral(rule));
    }

    private static (int Open, int Close, List<(int Start, int End)> Spans) RequireRuleSpans(string json, int index)
    {
        if (!TryFindRulesArray(json, out var open, out var close))
            throw new InvalidOperationException("The rules file has no \"rules\": [ ... ] array to edit.");

        var spans = RuleSpans(json, open, close);
        if (index < 0 || index >= spans.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The rules file has {spans.Count} rule(s).");

        return (open, close, spans);
    }

    // The [Start, End] character span of each top-level { ... } inside the rules array, in array
    // order — the same order Load returns, so an index means the same rule to both. Scanned with
    // the reader's rules (strings and comments skipped), so braces inside a pattern can't confuse it.
    private static List<(int Start, int End)> RuleSpans(string json, int open, int close)
    {
        var spans = new List<(int, int)>();
        var depth = 0;
        var start = -1;
        for (var i = open + 1; i < close; i++)
        {
            switch (json[i])
            {
                case '"':
                    i = SkipString(json, i);
                    break;
                case '/' when i + 1 < json.Length && (json[i + 1] == '/' || json[i + 1] == '*'):
                    i = SkipComment(json, i);
                    break;
                case '{':
                    if (depth++ == 0)
                        start = i;
                    break;
                case '[':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                        spans.Add((start, i));
                    break;
                case ']':
                    depth--;
                    break;
            }
        }

        return spans;
    }

    private static string RuleLiteral(ClassificationRule rule)
    {
        List<string> parts = [$"\"id\": {Str(rule.Id)}"];
        if (rule.ProcessPattern is { } process)
            parts.Add($"\"processPattern\": {Str(process)}");
        if (rule.MatchPattern is { } match)
            parts.Add($"\"matchPattern\": {Str(match)}");
        parts.Add($"\"category\": {Str(rule.Category)}");
        if (rule.Client is { } client)
            parts.Add($"\"client\": {Str(client)}");
        // Written only when set, so every rule already in the file keeps the shape it had.
        if (rule.ExcludeFrom is not ExcludeScope.None)
            parts.Add($"\"excludeFrom\": {Str(rule.ExcludeFrom.ToString().ToLowerInvariant())}");
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
          // Ordered, first match wins. processPattern / matchPattern are case-insensitive regexes.
          // matchPattern is tried against the window title and against the page address (host and
          // path, no ?query); either one matching is enough. Named groups (?<ticket>...) and
          // (?<client>...) in matchPattern extract those fields.
          "rules": [
            { "id": "halo-ticket", "matchPattern": "Ticket\\s*#?(?<ticket>\\d{3,})", "category": "Halo" },
            // HaloPSA's web app titles itself with unbranded breadcrumbs — "Tickets > Management >
            // <view>", ending in the ticket number when one is open — so the module names anchor
            // the match, and a trailing number is captured as the ticket.
            { "id": "halo-ticket-tab", "matchPattern": "^Tickets\\s*>.*>\\s*(?<ticket>\\d{3,})", "category": "Halo" },
            { "id": "halo-tab", "matchPattern": "^(Tickets|Clients|Users|Sites|Assets|Opportunities|Projects|Contracts|Suppliers|Invoices|Quotations|Reports|Configuration|Knowledge Base)\\s*>", "category": "Halo" },
            { "id": "halo", "matchPattern": "Halo\\s?PSA", "category": "Halo" },
            // IT Glue pages title themselves "<page> — IT Glue" (em-dash in the web app; hyphen and
            // en-dash allowed too), or lead with the product name.
            { "id": "itglue", "matchPattern": "(^|[-\\u2013\\u2014]\\s*)IT Glue\\b", "category": "IT Glue" },
            { "id": "screenconnect-client", "matchPattern": "^(?<client>.+?)\\s+[-\\u2013\\u2014].*(ScreenConnect|ConnectWise Control)", "category": "ScreenConnect" },
            { "id": "screenconnect", "matchPattern": "ScreenConnect|ConnectWise Control", "category": "ScreenConnect" },
            // Outlook wherever it's read: the desktop app (olk = new Outlook, outlook = classic) by
            // process, and OWA in a browser tab by its "Mail - <name> - Outlook" title shape. The
            // leading dash keeps a page merely mentioning Outlook from being claimed.
            { "id": "outlook-app", "processPattern": "^(olk|outlook)$", "category": "Outlook" },
            { "id": "owa", "matchPattern": "(^|[-\\u2013\\u2014]\\s*)Outlook\\b", "category": "Outlook" },
            // Teams window titles carry the focused chat/channel: "Chat | <name> | Microsoft Teams".
            // Capture that name as the subject so the rollup separates each conversation. Filed as
            // "Teams - Chat" so it reads apart from "Teams - Call" (which calls carry) on a
            // timesheet; a Teams window with no conversation in its title stays plain "Teams".
            { "id": "teams-chat", "processPattern": "^(ms-teams|msteams|Teams)$", "matchPattern": "^(?:Chat \\| )?(?<subject>.+?)\\s*\\| Microsoft Teams", "category": "Teams - Chat" },
            { "id": "teams", "processPattern": "^(ms-teams|msteams|Teams)$", "category": "Teams" },
            // Discord titles the focused channel, DM or view: "#channel | Server - Discord",
            // "@someone - Discord", "Friends - Discord". Capturing it as the subject separates each
            // conversation on the rollup; a bare "Discord" falls through to the rule below.
            { "id": "discord-channel", "processPattern": "^Discord$", "matchPattern": "^(?<subject>.+?)\\s*-\\s*Discord$", "category": "Discord" },
            { "id": "discord", "processPattern": "^Discord$", "category": "Discord" },
            // RingCentral ships under several executable names (the app, the older phone client,
            // meetings), so this matches the prefix rather than one exact name.
            { "id": "ringcentral", "processPattern": "^RingCentral", "category": "RingCentral" },
            { "id": "terminal", "processPattern": "^(WindowsTerminal|wt|OpenConsole|conhost|powershell|pwsh|cmd)$", "category": "Development" },
            { "id": "vscode", "processPattern": "^Code$", "category": "Development" },
            { "id": "visual-studio", "processPattern": "^devenv$", "category": "Development" }
            // No catch-all browser rule: a tab that matches nothing lands in Uncategorized, where
            // the triage tab can teach it a real rule — a visible gap beats time quietly filed
            // under a generic "Browsing".
          ]
        }
        """;
}
