using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// Rules that keep an activity out of an account of the day. Rollup tidies that tab while the
/// time still bills; Timesheet keeps it off the timesheet, the export, and the Tickets tab; All
/// does both. The Timeline never loses anything — it records what happened, not what gets billed.
/// </summary>
public class ExclusionRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock Block(
        string process, string title, int minutes, ExcludeScope scope, string category = "Personal")
        => new(
            new Block(T0, T0.AddMinutes(minutes), process, title),
            new Classification(category, null, null, null, "rule", scope));

    private static ClassifiedBlock Work(string title, int minutes)
        => new(
            new Block(T0.AddHours(2), T0.AddHours(2).AddMinutes(minutes), "msedge", title),
            new Classification("Halo", null, "495308", null, "halo"));

    private static string Page(params ClassifiedBlock[] blocks)
        => HtmlReportWriter.BuildMainInner(new DateOnly(2026, 8, 15), blocks, [], []);

    // ---- the rule reaching the classification ----

    [Theory]
    [InlineData(ExcludeScope.None, false, false)]
    [InlineData(ExcludeScope.Rollup, true, false)]
    [InlineData(ExcludeScope.Timesheet, false, true)]
    [InlineData(ExcludeScope.All, true, true)]
    public void AScope_DecidesWhichAccountsLeaveTheTimeOut(
        ExcludeScope scope, bool fromRollup, bool fromTimesheet)
    {
        var classifier = new Classifier([
            new ClassificationRule
            {
                Id = "yt", ProcessPattern = "^chrome$", Category = "Personal", ExcludeFrom = scope,
            },
        ]);

        var classification = classifier.Classify("chrome", "Some video - YouTube");
        Assert.Equal(fromRollup, classification.ExcludedFromRollup);
        Assert.Equal(fromTimesheet, classification.ExcludedFromTimesheet);
    }

    [Fact]
    public void AnOrdinaryRule_ExcludesNothing()
    {
        var classifier = new Classifier([
            new ClassificationRule { Id = "halo", ProcessPattern = "^msedge$", Category = "Halo" },
        ]);

        Assert.Equal(ExcludeScope.None, classifier.Classify("msedge", "Ticket 495308").ExcludeFrom);
    }

    [Fact]
    public void ActivityMatchingNoRule_ExcludesNothing()
    {
        // Uncategorized is a question still to answer, not a decision to leave time out.
        Assert.Equal(ExcludeScope.None, new Classifier([]).Classify("notepad", "untitled").ExcludeFrom);
    }

    // ---- Rollup scope ----

    [Theory]
    [InlineData(ExcludeScope.Rollup)]
    [InlineData(ExcludeScope.All)]
    public void ExcludingFromTheRollup_TakesTheRowOutOfIt(ExcludeScope scope)
    {
        var rows = RollupBuilder.Build([
            Block("chrome", "Some video - YouTube", 40, scope),
            Work("Ticket 495308", 25),
        ]);

        Assert.Equal("Halo", Assert.Single(rows).Category);
    }

    [Fact]
    public void ExcludingFromTheTimesheetOnly_LeavesTheRollupRowInPlace()
    {
        // The Rollup is where the day went; only the timesheet was told not to bill it.
        var rows = RollupBuilder.Build([
            Block("chrome", "Some video - YouTube", 40, ExcludeScope.Timesheet),
            Work("Ticket 495308", 25),
        ]);

        Assert.Contains(rows, r => r.Category == "Personal");
    }

    // ---- Timesheet scope ----

    [Theory]
    [InlineData(ExcludeScope.Timesheet)]
    [InlineData(ExcludeScope.All)]
    public void ExcludingFromTheTimesheet_EarnsNoLineAndNoExportEntry(ExcludeScope scope)
    {
        ClassifiedBlock[] blocks = [Block("chrome", "Some video - YouTube", 40, scope), Work("Ticket 495308", 25)];

        Assert.DoesNotContain(SuggestionSlotBuilder.Build(blocks), s => s.Category == "Personal");
        // The export shares the Timesheet's builder precisely so it cannot disagree with the
        // screen that reviewed it — this is the assertion that keeps that true.
        Assert.DoesNotContain(JsonExportWriter.BuildEntries(blocks, []), e => e.Slot.Category == "Personal");
    }

    [Fact]
    public void ExcludingFromTheRollupOnly_StillEarnsItsTimesheetLine()
    {
        var slots = SuggestionSlotBuilder.Build([Block("chrome", "Long personal call", 40, ExcludeScope.Rollup)]);

        Assert.Contains(slots, s => s.Category == "Personal");
    }

    [Theory]
    [InlineData(ExcludeScope.Timesheet)]
    [InlineData(ExcludeScope.All)]
    public void ExcludingFromTheTimesheet_ContributesNoTicketRow(ExcludeScope scope)
    {
        // Even when the excluded window's title happens to carry a ticket number: the Tickets tab
        // is the "what do I bill" list, so it follows the timesheet.
        var excludedWithTicket = new ClassifiedBlock(
            new Block(T0, T0.AddMinutes(30), "msedge", "Ticket 495308 (Personal)"),
            new Classification("Personal", null, "495308", null, "rule", scope));

        Assert.Empty(TicketsBuilder.Build([excludedWithTicket]));
    }

    [Fact]
    public void ExcludingFromTheRollupOnly_KeepsItsTicketRow()
        => Assert.Single(TicketsBuilder.Build([
            new ClassifiedBlock(
                new Block(T0, T0.AddMinutes(30), "msedge", "Ticket 495308"),
                new Classification("Halo", null, "495308", null, "rule", ExcludeScope.Rollup))]));

    // ---- the summary cards ----

    [Fact]
    public void TimesheetExclusions_LeaveActiveAndShowInTheExcludedCard()
    {
        var html = Page(Block("chrome", "Some video - YouTube", 40, ExcludeScope.Timesheet), Work("Ticket", 20));

        // Active is the 20 minutes of work; Total is all 60 minutes recorded; the Excluded card
        // accounts for the difference rather than letting 40 minutes silently vanish.
        Assert.Contains("<div class=\"v\">1h 00m</div><div class=\"l\">Total</div>", html);
        Assert.Contains("<div class=\"v\">20m</div><div class=\"l\">Active</div>", html);
        Assert.Contains("<div class=\"v\">40m</div><div class=\"l\">Excluded</div>", html);
    }

    [Fact]
    public void ARollupOnlyExclusion_IsStillActiveTime()
    {
        // It stays on the timesheet, so calling it anything but active would contradict the file.
        var html = Page(Block("chrome", "Long personal call", 40, ExcludeScope.Rollup), Work("Ticket", 20));

        Assert.Contains("<div class=\"v\">1h 00m</div><div class=\"l\">Active</div>", html);
        Assert.DoesNotContain(">Excluded</div>", html);
    }

    [Fact]
    public void ADayWithNothingExcluded_ShowsNoExcludedCard()
        => Assert.DoesNotContain(">Excluded</div>", Page(Work("Ticket 495308", 20)));

    // ---- what the Timeline keeps ----

    [Theory]
    [InlineData(ExcludeScope.Rollup, "not in rollup")]
    [InlineData(ExcludeScope.Timesheet, "not on timesheet")]
    [InlineData(ExcludeScope.All, "excluded")]
    public void TheTimeline_KeepsExcludedActivity_AndNamesWhatItIsMissingFrom(
        ExcludeScope scope, string tag)
    {
        var html = Page(Block("chrome", "Some video - YouTube", 40, scope));

        Assert.Contains("Some video - YouTube", html);   // the day's record keeps it
        Assert.Contains("tl-excluded", html);
        Assert.Contains($"<span class=\"tl-ex-tag\">{tag}</span>", html);
    }

    [Fact]
    public void ExcludedActivity_IsNotCountedAsLostTime()
    {
        // It carries a category, so it is accounted for — "lost" means nobody said what it was.
        Assert.Contains("Nothing unaccounted for",
            Page(Block("chrome", "Some video - YouTube", 40, ExcludeScope.All)));
    }

    // ---- the rules file ----

    [Theory]
    [InlineData(ExcludeScope.Rollup, "rollup")]
    [InlineData(ExcludeScope.Timesheet, "timesheet")]
    [InlineData(ExcludeScope.All, "all")]
    public void AnExcludingRule_RoundTripsThroughTheRulesFile(ExcludeScope scope, string written)
    {
        var json = RulesFile.WithRule(
            """{ "rules": [] }""",
            new ClassificationRule
            {
                Id = "yt", ProcessPattern = "^chrome$", Category = "Personal", ExcludeFrom = scope,
            });

        Assert.Contains($"\"excludeFrom\": \"{written}\"", json);
        Assert.Equal(scope, Assert.Single(RulesFile.Parse(json)).ExcludeFrom);
    }

    [Fact]
    public void AnOrdinaryRule_WritesNoExcludeKey()
    {
        // Every rule already in a user's file keeps exactly the shape it had.
        var json = RulesFile.WithRule(
            """{ "rules": [] }""",
            new ClassificationRule { Id = "halo", ProcessPattern = "^msedge$", Category = "Halo" });

        Assert.DoesNotContain("exclude", json);
        Assert.Equal(ExcludeScope.None, Assert.Single(RulesFile.Parse(json)).ExcludeFrom);
    }

    [Fact]
    public void AMisspelledScope_CostsThatRuleItsExclusion_NotTheFileItsRules()
    {
        // rules.json is hand-editable; a strict enum would throw, and a failed load reads as no
        // rules at all — losing every rule the user has over one typo.
        var rules = RulesFile.Parse(
            """
            {
              "rules": [
                { "id": "a", "processPattern": "^chrome$", "category": "Personal", "excludeFrom": "rollupp" },
                { "id": "b", "processPattern": "^msedge$", "category": "Halo" }
              ]
            }
            """);

        Assert.Equal(["a", "b"], rules.Select(r => r.Id));
        Assert.Equal(ExcludeScope.None, rules[0].ExcludeFrom);
    }

    [Fact]
    public void ARuleDraftedFromTheUncategorizedTab_CarriesTheScopeItWasSavedWith()
        => Assert.Equal(
            ExcludeScope.Timesheet,
            RuleDraft.Create("chrome", "Some video - YouTube", RuleMatch.App, "Personal", null, ExcludeScope.Timesheet)
                .ExcludeFrom);

    [Fact]
    public void ARuleDraftedWithoutExcluding_StaysCounted()
        => Assert.Equal(
            ExcludeScope.None,
            RuleDraft.Create("msedge", "Ticket 495308", RuleMatch.Window, "Halo").ExcludeFrom);
}
