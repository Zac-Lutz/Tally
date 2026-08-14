using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>Editing and deleting rules in place: the text edits behind the live view's Rules tab.</summary>
public class RuleEditingTests
{
    private const string ThreeRules =
        """
        {
          // header comment — must survive every edit
          "rules": [
            { "id": "a", "titlePattern": "Alpha", "category": "A" },
            // note about b (and about c below it)
            { "id": "b", "processPattern": "^b$", "category": "B" },
            { "id": "c", "titlePattern": "\\[work\\] > done", "category": "C" }
          ]
        }
        """;

    // ---- WithoutRuleAt ----

    [Fact]
    public void Remove_Middle_KeepsNeighboursAndComments_AndStillLoads()
    {
        var updated = RulesFile.WithoutRuleAt(ThreeRules, 1);

        Assert.Contains("// header comment", updated);
        Assert.Contains("// note about b", updated);   // comments are never guessed at
        Assert.Equal(["a", "c"], LoadRules(updated).Select(r => r.Id));
    }

    [Fact]
    public void Remove_First_TakesItsCommaAndLine()
    {
        var updated = RulesFile.WithoutRuleAt(ThreeRules, 0);

        Assert.Equal(["b", "c"], LoadRules(updated).Select(r => r.Id));
        Assert.DoesNotContain("Alpha", updated);
        Assert.DoesNotContain("\n\n    //", updated);   // no emptied line left behind
    }

    [Fact]
    public void Remove_Last_LeavesADocumentTheReaderAccepts()
    {
        var updated = RulesFile.WithoutRuleAt(ThreeRules, 2);

        Assert.Equal(["a", "b"], LoadRules(updated).Select(r => r.Id));
    }

    [Fact]
    public void Remove_TheOnlyRule_LeavesAnEmptyButValidRulesArray()
    {
        var updated = RulesFile.WithoutRuleAt("""{ "rules": [ { "id": "solo", "titlePattern": "x", "category": "S" } ] }""", 0);

        Assert.Empty(LoadRules(updated));
    }

    [Fact]
    public void Remove_IsNotFooledByBracesInsideAPattern()
    {
        var json = """{ "rules": [ { "id": "curly", "titlePattern": "\\{\\}", "category": "X" }, { "id": "plain", "titlePattern": "y", "category": "Y" } ] }""";

        var updated = RulesFile.WithoutRuleAt(json, 0);

        Assert.Equal("plain", LoadRules(updated).Single().Id);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Remove_OutOfRange_Throws(int index)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RulesFile.WithoutRuleAt(ThreeRules, index));

    [Fact]
    public void Remove_WithNoRulesArray_Throws()
        => Assert.Throws<InvalidOperationException>(() => RulesFile.WithoutRuleAt("{ \"other\": 1 }", 0));

    [Fact]
    public void Remove_FromTheShippedDefaults_RoundTrips()
    {
        var before = LoadRules(RulesFile.DefaultRulesJson);

        var after = LoadRules(RulesFile.WithoutRuleAt(RulesFile.DefaultRulesJson, 0));

        Assert.Equal(before.Count - 1, after.Count);
        Assert.Equal(before.Skip(1).Select(r => r.Id), after.Select(r => r.Id));
    }

    // ---- WithRuleReplacedAt ----

    [Fact]
    public void Replace_RewritesJustThatRule_InItsPlace()
    {
        var edited = new ClassificationRule { Id = "b", ProcessPattern = "^beta$", Category = "Beta" };

        var updated = RulesFile.WithRuleReplacedAt(ThreeRules, 1, edited);

        var rules = LoadRules(updated);
        Assert.Equal(["a", "b", "c"], rules.Select(r => r.Id));   // order untouched — it's first-match-wins
        Assert.Equal("Beta", rules[1].Category);
        Assert.Equal("^beta$", rules[1].ProcessPattern);
        Assert.Contains("// note about b", updated);
        Assert.Equal("A", rules[0].Category);
        Assert.Equal("C", rules[2].Category);
    }

    [Fact]
    public void Replace_RoundTripsARegexFullOfEscapes()
    {
        var edited = new ClassificationRule
        {
            Id = "c",
            TitlePattern = """^(Tickets|Clients)\s*>.*"quoted"\\end""",
            Category = "C",
        };

        var updated = RulesFile.WithRuleReplacedAt(ThreeRules, 2, edited);

        Assert.Equal(edited.TitlePattern, LoadRules(updated)[2].TitlePattern);
    }

    [Fact]
    public void Replace_CanAddAndDropOptionalFields()
    {
        // b gains a client and a title pattern, loses its process pattern.
        var edited = new ClassificationRule { Id = "b", TitlePattern = "Beta", Category = "B", Client = "Acme" };

        var rules = LoadRules(RulesFile.WithRuleReplacedAt(ThreeRules, 1, edited));

        Assert.Equal("Acme", rules[1].Client);
        Assert.Equal("Beta", rules[1].TitlePattern);
        Assert.Null(rules[1].ProcessPattern);
    }

    [Fact]
    public void Replace_OutOfRange_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RulesFile.WithRuleReplacedAt(
            ThreeRules, 9, new ClassificationRule { Id = "x", TitlePattern = "x", Category = "X" }));

    // ---- WithCategoryRenamed ----

    private const string TwoAsOneB =
        """
        {
          "rules": [
            // both a-rules move together
            { "id": "a1", "titlePattern": "Alpha", "category": "A" },
            { "id": "b1", "processPattern": "^b$", "category": "B" },
            { "id": "a2", "titlePattern": "Alef", "category": "A" }
          ]
        }
        """;

    [Fact]
    public void RenameCategory_RefilesEveryMatchingRule_InPlace()
    {
        var updated = RulesFile.WithCategoryRenamed(TwoAsOneB, "A", "Admin", out var renamed);

        Assert.Equal(2, renamed);
        var rules = LoadRules(updated);
        Assert.Equal(["a1", "b1", "a2"], rules.Select(r => r.Id));   // order untouched
        Assert.Equal(["Admin", "B", "Admin"], rules.Select(r => r.Category));
        Assert.Equal("Alpha", rules[0].TitlePattern);                // everything else kept
        Assert.Contains("// both a-rules move together", updated);
    }

    [Fact]
    public void RenameCategory_NoMatch_ChangesNothing()
    {
        var updated = RulesFile.WithCategoryRenamed(TwoAsOneB, "Zzz", "Admin", out var renamed);

        Assert.Equal(0, renamed);
        Assert.Equal(TwoAsOneB, updated);
    }

    [Fact]
    public void RenameCategory_IsExactMatch_SoACasingVariantIsItsOwnCategory()
    {
        RulesFile.WithCategoryRenamed(TwoAsOneB, "a", "Admin", out var renamed);

        Assert.Equal(0, renamed);
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
