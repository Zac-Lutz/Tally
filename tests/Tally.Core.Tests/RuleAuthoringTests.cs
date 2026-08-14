using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>Unclassified triage: grouping the leftovers, drafting a rule, writing it to the file.</summary>
public class RuleAuthoringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock Unclassified(string process, string title, double minutes)
        => new(
            new Block(T0, T0.AddMinutes(minutes), process, title),
            new Classification(Classification.Unclassified, null, null, null, null));

    // ---- UnclassifiedBuilder ----

    [Fact]
    public void Build_GroupsTheSameActivityIntoOneRow_AndSumsIt()
    {
        var rows = UnclassifiedBuilder.Build(
        [
            Unclassified("notepad", "Runbook.txt - Notepad", 10),
            Unclassified("notepad", "Runbook.txt - Notepad", 5),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("notepad", row.ProcessName);
        Assert.Equal(TimeSpan.FromMinutes(15), row.Time);
    }

    [Fact]
    public void Build_IgnoresClassifiedBlocks()
    {
        var classified = new ClassifiedBlock(
            new Block(T0, T0.AddMinutes(30), "devenv", "Tally"),
            new Classification("Development", null, null, null, "vs"));

        Assert.Empty(UnclassifiedBuilder.Build([classified]));
    }

    [Fact]
    public void Build_NormalizesBrowserNoise_SoOneTabIsOneRow()
    {
        var rows = UnclassifiedBuilder.Build(
        [
            Unclassified("brave", "Pricing - Acme and 3 more pages", 8),
            Unclassified("brave", "Pricing - Acme", 4),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Pricing - Acme", row.Title);
        Assert.Equal(TimeSpan.FromMinutes(12), row.Time);
    }

    [Fact]
    public void Build_DropsSubMinuteNoise_AndOrdersByTimeSpent()
    {
        var rows = UnclassifiedBuilder.Build(
        [
            Unclassified("notepad", "Scratch", 5),
            Unclassified("calc", "Calculator", 20),
            Unclassified("explorer", "Downloads", 0.5),
        ]);

        Assert.Equal(["Calculator", "Scratch"], rows.Select(r => r.Title));
    }

    [Fact]
    public void Build_SeparatesTheSameTitleInDifferentApps()
    {
        var rows = UnclassifiedBuilder.Build(
        [
            Unclassified("notepad", "Notes", 10),
            Unclassified("wordpad", "Notes", 10),
        ]);

        Assert.Equal(2, rows.Count);
    }

    // ---- RuleDraft ----

    [Fact]
    public void Draft_ForAnApp_MatchesTheProcessOnly()
    {
        var rule = RuleDraft.Create("notepad", "Runbook.txt - Notepad", RuleMatch.App, "Documentation");

        Assert.Equal("^notepad$", rule.ProcessPattern);
        Assert.Null(rule.TitlePattern);          // any window of that app
        Assert.Equal("Documentation", rule.Category);
    }

    [Fact]
    public void Draft_ForAWindow_MatchesTheProcessAndThatTitle()
    {
        var rule = RuleDraft.Create("brave", "Pricing - Acme and 3 more pages", RuleMatch.Window, "Browsing");

        Assert.Equal("^brave$", rule.ProcessPattern);
        Assert.Equal("Pricing - Acme", rule.TitlePattern);   // normalized, as the row displayed it
    }

    [Fact]
    public void Draft_EscapesRegexCharactersSoTheTitleIsMatchedLiterally()
    {
        var rule = RuleDraft.Create("app", "Cost (2026) + notes [draft]", RuleMatch.Window, "Admin");

        var classifier = new Classifier([rule]);
        Assert.Equal("Admin", classifier.Classify("app", "Cost (2026) + notes [draft]").Category);
        // The metacharacters are literal, so a title that only matches them as regex doesn't hit.
        Assert.True(classifier.Classify("app", "Cost 2026  notes d").IsUnclassified);
    }

    [Fact]
    public void Draft_EscapesRegexCharactersInTheProcessName()
    {
        var rule = RuleDraft.Create("my.app+x", "Anything", RuleMatch.App, "Admin");

        Assert.Equal(@"^my\.app\+x$", rule.ProcessPattern);
    }

    [Fact]
    public void Draft_GivesAReadableId_ThatAvoidsCollidingWithExistingOnes()
    {
        var first = RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "Documentation");
        Assert.Equal("documentation-notepad", first.Id);

        var second = RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "Documentation", [first.Id]);
        Assert.Equal("documentation-notepad-2", second.Id);
    }

    [Fact]
    public void ManualId_IsTheCategorySlug_Uniquified()
    {
        Assert.Equal("it-glue", RuleDraft.ManualId("IT Glue"));
        Assert.Equal("it-glue-2", RuleDraft.ManualId("IT Glue", ["it-glue"]));
        Assert.Equal("rule", RuleDraft.ManualId("???"));   // nothing sluggable still gets an id
    }

    [Fact]
    public void Draft_WithoutACategory_IsRejected()
        => Assert.Throws<ArgumentException>(() => RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "   "));

    [Fact]
    public void Draft_WithNoProcessName_FallsBackToMatchingTheTitle()
    {
        var rule = RuleDraft.Create("", "Daily standup", RuleMatch.App, "Meetings");

        Assert.Null(rule.ProcessPattern);
        Assert.Equal("Daily standup", rule.TitlePattern);
    }

    // ---- RulesFile.WithRule ----

    [Fact]
    public void WithRule_KeepsTheCommentsAndEveryExistingRule()
    {
        var rule = RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "Documentation");

        var updated = RulesFile.WithRule(RulesFile.DefaultRulesJson, rule);

        Assert.Contains("// Ordered, first match wins.", updated);
        Assert.Contains("\"id\": \"halo-ticket\"", updated);
        Assert.Contains("\"id\": \"documentation-notepad\"", updated);
    }

    [Fact]
    public void WithRule_PutsABroadAppRuleLast_SoItCannotShadowWhatWasAlreadyThere()
    {
        var rule = RuleDraft.Create("chrome", "Anything", RuleMatch.App, "Admin");

        var updated = RulesFile.WithRule(RulesFile.DefaultRulesJson, rule);

        Assert.True(updated.IndexOf("admin-chrome", StringComparison.Ordinal)
                    > updated.IndexOf("\"visual-studio\"", StringComparison.Ordinal));
        Assert.Contains("\"visual-studio\"", updated);   // guard: the anchor rule still exists
    }

    [Fact]
    public void WithRule_PutsASpecificWindowRuleFirst_SoItBeatsTheGenericOnes()
    {
        var rule = RuleDraft.Create("chrome", "Acme Renewal", RuleMatch.Window, "Acme");

        var updated = RulesFile.WithRule(RulesFile.DefaultRulesJson, rule);

        Assert.True(updated.IndexOf("acme-acme-renewal", StringComparison.Ordinal)
                    < updated.IndexOf("\"halo-ticket\"", StringComparison.Ordinal));
    }

    [Fact]
    public void WithRule_HandlesAnEmptyRulesArray()
    {
        var rule = RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "Documentation");

        var updated = RulesFile.WithRule("{ \"rules\": [] }", rule);

        Assert.Equal("Documentation", LoadRules(updated).Single().Category);
    }

    [Fact]
    public void WithRule_HandlesATrailingCommaWithoutDoublingIt()
    {
        var json = "{ \"rules\": [\n    { \"id\": \"a\", \"processPattern\": \"^a$\", \"category\": \"A\" },\n  ] }";

        var updated = RulesFile.WithRule(json, RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "B"));

        Assert.DoesNotContain(",,", updated);
        Assert.Equal(2, LoadRules(updated).Count);
    }

    [Fact]
    public void WithRule_IsNotFooledByBracketsInsideAPatternOrAComment()
    {
        var json = """
            {
              // rules below — mind the ] in this comment
              "rules": [
                { "id": "brackets", "titlePattern": "^\\[work\\]", "category": "Admin" }
              ]
            }
            """;

        var updated = RulesFile.WithRule(json, RuleDraft.Create("notepad", "Runbook", RuleMatch.App, "Documentation"));

        var rules = LoadRules(updated);
        Assert.Equal(2, rules.Count);
        Assert.Equal("brackets", rules[0].Id);
    }

    [Fact]
    public void WithRule_OnADocumentWithNoRulesArray_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => RulesFile.WithRule("{ \"other\": 1 }", RuleDraft.Create("notepad", "R", RuleMatch.App, "A")));

    // ---- End to end: triage row -> rule -> the day reclassifies ----

    [Fact]
    public void ARuleSavedFromATriageRow_ClassifiesThatActivityOnTheNextReport()
    {
        var day = new[] { Unclassified("notepad", "Runbook.txt - Notepad", 25) };
        var row = Assert.Single(UnclassifiedBuilder.Build(day));

        var rule = RuleDraft.Create(row.ProcessName, row.Title, RuleMatch.App, "Documentation");
        var rules = LoadRules(RulesFile.WithRule(RulesFile.DefaultRulesJson, rule));

        var classification = new Classifier(rules).Classify("notepad", "Runbook.txt - Notepad");

        Assert.Equal("Documentation", classification.Category);
        Assert.Equal("documentation-notepad", classification.RuleId);
    }

    // Round-trips the edited document through the real loader, so a test only passes if what was
    // written is a file the app can actually read back.
    private static IReadOnlyList<ClassificationRule> LoadRules(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tally-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return RulesFile.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
