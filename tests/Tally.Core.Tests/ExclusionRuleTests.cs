using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// Rules that declare an activity isn't work to account for. Excluded time leaves the Rollup, the
/// Timesheet, and the export, and stays in the Timeline — the Timeline being the record of what
/// actually happened rather than what gets billed.
/// </summary>
public class ExclusionRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock Block(
        string process, string title, int minutes, bool excluded, string category = "Personal")
        => new(
            new Block(T0, T0.AddMinutes(minutes), process, title),
            new Classification(category, null, null, null, "rule", excluded));

    private static ClassifiedBlock Work(string title, int minutes)
        => new(
            new Block(T0.AddHours(2), T0.AddHours(2).AddMinutes(minutes), "msedge", title),
            new Classification("Halo", null, null, null, "halo"));

    // ---- the rule reaching the classification ----

    [Fact]
    public void ARuleMarkedExclude_MarksWhatItMatches()
    {
        var classifier = new Classifier([
            new ClassificationRule { Id = "yt", ProcessPattern = "^chrome$", Category = "Personal", Exclude = true },
        ]);

        Assert.True(classifier.Classify("chrome", "Some video - YouTube").Excluded);
    }

    [Fact]
    public void AnOrdinaryRule_ExcludesNothing()
    {
        var classifier = new Classifier([
            new ClassificationRule { Id = "halo", ProcessPattern = "^msedge$", Category = "Halo" },
        ]);

        Assert.False(classifier.Classify("msedge", "Ticket 495308").Excluded);
    }

    [Fact]
    public void ActivityMatchingNoRule_ExcludesNothing()
    {
        // Uncategorized is a question still to answer, not a decision to leave time out.
        Assert.False(new Classifier([]).Classify("notepad", "untitled").Excluded);
    }

    // ---- where excluded time must not appear ----

    [Fact]
    public void ExcludedActivity_NeverReachesTheRollup()
    {
        var rows = RollupBuilder.Build([
            Block("chrome", "Some video - YouTube", 40, excluded: true),
            Work("Ticket 495308", 25),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Halo", row.Category);
    }

    [Fact]
    public void ExcludedActivity_NeverEarnsATimesheetLine()
    {
        var slots = SuggestionSlotBuilder.Build([
            Block("chrome", "Some video - YouTube", 40, excluded: true),
            Work("Ticket 495308", 25),
        ]);

        Assert.DoesNotContain(slots, s => s.Category == "Personal");
        Assert.Contains(slots, s => s.Category == "Halo");
    }

    [Fact]
    public void ExcludedActivity_NeverReachesTheExport()
    {
        // The export shares the Timesheet's builder precisely so it cannot disagree with the
        // screen that reviewed it — this is the assertion that keeps that true.
        var entries = JsonExportWriter.BuildEntries(
            [Block("chrome", "Some video - YouTube", 40, excluded: true), Work("Ticket 495308", 25)],
            []);

        Assert.DoesNotContain(entries, e => e.Slot.Category == "Personal");
        Assert.Contains(entries, e => e.Slot.Category == "Halo");
    }

    [Fact]
    public void ExcludedActivity_ContributesNoTicketRow()
    {
        // Even when the excluded window's title happens to carry a ticket number.
        var excludedWithTicket = new ClassifiedBlock(
            new Block(T0, T0.AddMinutes(30), "msedge", "Ticket 495308 (Personal)"),
            new Classification("Personal", null, "495308", null, "rule", Excluded: true));

        Assert.Empty(TicketsBuilder.Build([excludedWithTicket]));
    }

    [Fact]
    public void ExcludedActivity_IsNotCountedAsLostTime()
    {
        // It carries a category, so it is accounted for — "lost" means nobody said what it was.
        var html = HtmlReportWriter.BuildMainInner(
            new DateOnly(2026, 8, 15),
            [Block("chrome", "Some video - YouTube", 40, excluded: true)],
            [],
            []);

        Assert.Contains("Nothing unaccounted for", html);
    }

    // ---- where it must still appear ----

    [Fact]
    public void ExcludedActivity_StillShowsInTheTimeline_AndSaysWhyItIsMissingElsewhere()
    {
        var html = HtmlReportWriter.BuildMainInner(
            new DateOnly(2026, 8, 15),
            [Block("chrome", "Some video - YouTube", 40, excluded: true), Work("Ticket 495308", 25)],
            [],
            []);

        Assert.Contains("Some video - YouTube", html);   // the day's record keeps it
        Assert.Contains("tl-excluded", html);            // and marks it as deliberately left out
    }

    [Fact]
    public void ExcludedTime_LeavesActiveButKeepsTheDayTotalHonest()
    {
        var html = HtmlReportWriter.BuildMainInner(
            new DateOnly(2026, 8, 15),
            [Block("chrome", "Some video - YouTube", 40, excluded: true), Work("Ticket 495308", 20)],
            [],
            []);

        // Active is the 20 minutes of work; Total is all 60 minutes recorded; the Excluded card
        // accounts for the difference rather than letting 40 minutes silently vanish.
        Assert.Contains("<div class=\"v\">1h 00m</div><div class=\"l\">Total</div>", html);
        Assert.Contains("<div class=\"v\">20m</div><div class=\"l\">Active</div>", html);
        Assert.Contains("<div class=\"v\">40m</div><div class=\"l\">Excluded</div>", html);
    }

    [Fact]
    public void ADayWithNothingExcluded_ShowsNoExcludedCard()
    {
        var html = HtmlReportWriter.BuildMainInner(
            new DateOnly(2026, 8, 15), [Work("Ticket 495308", 20)], [], []);

        Assert.DoesNotContain(">Excluded</div>", html);
    }

    // ---- the rules file ----

    [Fact]
    public void AnExcludingRule_RoundTripsThroughTheRulesFile()
    {
        var json = RulesFile.WithRule(
            """{ "rules": [] }""",
            new ClassificationRule { Id = "yt", ProcessPattern = "^chrome$", Category = "Personal", Exclude = true });

        Assert.Contains("\"exclude\": true", json);
        Assert.True(Assert.Single(RulesFile.Parse(json)).Exclude);
    }

    [Fact]
    public void AnOrdinaryRule_WritesNoExcludeKey()
    {
        // Every rule already in a user's file keeps exactly the shape it had.
        var json = RulesFile.WithRule(
            """{ "rules": [] }""",
            new ClassificationRule { Id = "halo", ProcessPattern = "^msedge$", Category = "Halo" });

        Assert.DoesNotContain("exclude", json);
        Assert.False(Assert.Single(RulesFile.Parse(json)).Exclude);
    }

    [Fact]
    public void ARuleDraftedFromTheUncategorizedTab_CarriesTheExcludeItWasSavedWith()
    {
        var rule = RuleDraft.Create(
            "chrome", "Some video - YouTube", RuleMatch.App, "Personal", null, exclude: true);

        Assert.True(rule.Exclude);
    }

    [Fact]
    public void ARuleDraftedWithoutExcluding_StaysCounted()
        => Assert.False(RuleDraft.Create("msedge", "Ticket 495308", RuleMatch.Window, "Halo").Exclude);
}
