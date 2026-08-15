using System.Text.RegularExpressions;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>Which accounts of the day a rule's matching activity stays out of.</summary>
public enum ExcludeScope
{
    /// <summary>Counted everywhere — what an ordinary rule does.</summary>
    None,

    /// <summary>Kept out of the Rollup only. The time is still work: it stays on the Timesheet,
    /// in the export, and in the Active total. This is tidying one view, not disowning time.</summary>
    Rollup,

    /// <summary>Kept off the Timesheet, out of the export, and off the Tickets tab — none of it
    /// is time to bill — while the Rollup still shows where the day went.</summary>
    Timesheet,

    /// <summary>
    /// Kept off the Timeline. The Timeline is the blow-by-blow record of what was on screen, so
    /// this is for activity that is simply noise to read past — it still counts everywhere else.
    /// </summary>
    Timeline,

    /// <summary>All three: no account of the day mentions it.</summary>
    All,
}

public sealed record ClassificationRule
{
    public required string Id { get; init; }

    /// <summary>Regex matched against the process name (no extension). Null = any process.</summary>
    public string? ProcessPattern { get; init; }

    /// <summary>
    /// Regex matched against the window title. Named groups <c>(?&lt;ticket&gt;)</c>,
    /// <c>(?&lt;client&gt;)</c>, and <c>(?&lt;subject&gt;)</c> are extracted into the
    /// classification. Null = any title.
    /// </summary>
    public string? TitlePattern { get; init; }

    /// <summary>
    /// Regex matched against the page the browser was showing, as stored — host and path, no
    /// scheme and no query string (see <see cref="UrlSanitizer"/>). Named groups work as they do
    /// in <see cref="TitlePattern"/>. Null = any page; set, it can only ever match a browser
    /// block, because nothing else has a page.
    /// </summary>
    public string? UrlPattern { get; init; }

    public required string Category { get; init; }

    /// <summary>Static client assignment; a <c>(?&lt;client&gt;)</c> capture in TitlePattern wins over this.</summary>
    public string? Client { get; init; }

    /// <summary>
    /// Which of the day's accounts leave this activity out. The Timeline still draws it whatever
    /// this says, because the Timeline is the record of what actually happened rather than of
    /// what gets billed.
    /// </summary>
    public ExcludeScope ExcludeFrom { get; init; }
}

/// <summary>Ordered, first-match-wins rule evaluation over (process, title).</summary>
public sealed class Classifier
{
    // Rules are user-edited regexes; a timeout keeps a pathological pattern from wedging reports.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly List<(ClassificationRule Rule, Regex? Process, Regex? Title, Regex? Url)> _rules;

    public Classifier(IEnumerable<ClassificationRule> rules)
    {
        _rules = rules
            .Select(rule => (rule, Compile(rule.ProcessPattern), Compile(rule.TitlePattern), Compile(rule.UrlPattern)))
            .ToList();
    }

    /// <summary>
    /// The first rule whose every pattern matches. <paramref name="url"/> is the page a browser
    /// block was showing, when one was captured; a rule naming a page can only match a block that
    /// has one.
    /// </summary>
    public Classification Classify(string processName, string title, string? url = null)
    {
        foreach (var (rule, processRegex, titleRegex, urlRegex) in _rules)
        {
            // A rule with no patterns would match everything; treat it as inert.
            if (processRegex is null && titleRegex is null && urlRegex is null)
                continue;

            if (processRegex is not null && !SafeIsMatch(processRegex, processName))
                continue;

            Match? titleMatch = null;
            if (titleRegex is not null)
            {
                titleMatch = SafeMatch(titleRegex, title);
                if (titleMatch is not { Success: true })
                    continue;
            }

            Match? urlMatch = null;
            if (urlRegex is not null)
            {
                // No page means nothing for the pattern to be true of — a rule about a website
                // must not quietly match the file explorer.
                if (url is null)
                    continue;

                urlMatch = SafeMatch(urlRegex, url);
                if (urlMatch is not { Success: true })
                    continue;
            }

            // The title is the more specific evidence when a rule reads both, so its captures win
            // and the page fills in whatever the title didn't name.
            var ticket = GroupValue(titleMatch, "ticket") ?? GroupValue(urlMatch, "ticket");
            var client = GroupValue(titleMatch, "client") ?? GroupValue(urlMatch, "client") ?? rule.Client;
            var subject = GroupValue(titleMatch, "subject") ?? GroupValue(urlMatch, "subject");
            return new Classification(rule.Category, client, ticket, subject, rule.Id, rule.ExcludeFrom);
        }

        return new Classification(Classification.Unclassified, null, null, null, null);
    }

    private static Regex? Compile(string? pattern)
        => pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);

    private static string? GroupValue(Match? match, string group)
        => match?.Groups[group] is { Success: true } g && g.Value.Trim() is { Length: > 0 } value
            ? value
            : null;

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Match? SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
