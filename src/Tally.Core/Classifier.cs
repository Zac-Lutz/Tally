using System.Text.RegularExpressions;
using Tally.Core.Models;

namespace Tally.Core;

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

    public required string Category { get; init; }

    /// <summary>Static client assignment; a <c>(?&lt;client&gt;)</c> capture in TitlePattern wins over this.</summary>
    public string? Client { get; init; }

    /// <summary>
    /// Matching activity is not work to account for: it stays out of the Rollup, the Timesheet,
    /// and the export. The Timeline still draws it, because the Timeline is the record of what
    /// actually happened rather than what gets billed.
    /// </summary>
    public bool Exclude { get; init; }
}

/// <summary>Ordered, first-match-wins rule evaluation over (process, title).</summary>
public sealed class Classifier
{
    // Rules are user-edited regexes; a timeout keeps a pathological pattern from wedging reports.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly List<(ClassificationRule Rule, Regex? Process, Regex? Title)> _rules;

    public Classifier(IEnumerable<ClassificationRule> rules)
    {
        _rules = rules
            .Select(rule => (rule, Compile(rule.ProcessPattern), Compile(rule.TitlePattern)))
            .ToList();
    }

    public Classification Classify(string processName, string title)
    {
        foreach (var (rule, processRegex, titleRegex) in _rules)
        {
            // A rule with no patterns would match everything; treat it as inert.
            if (processRegex is null && titleRegex is null)
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

            var ticket = GroupValue(titleMatch, "ticket");
            var client = GroupValue(titleMatch, "client") ?? rule.Client;
            var subject = GroupValue(titleMatch, "subject");
            return new Classification(rule.Category, client, ticket, subject, rule.Id, rule.Exclude);
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
