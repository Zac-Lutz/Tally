using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// The one pattern a rule matches with, tried against the window title and against the page the
/// browser was showing. A tab's title changes constantly while its address stays put, so the site
/// is often the truer thing to key a rule on — and reading both means a rule written either way
/// keeps working.
/// </summary>
public class UrlRuleTests
{
    private static Classifier With(params ClassificationRule[] rules) => new(rules);

    private static ClassificationRule Rule(string id, string pattern, string category)
        => new() { Id = id, MatchPattern = pattern, Category = category };

    // ---- matching ----

    [Fact]
    public void APageRule_MatchesEveryPageOnThatSite()
    {
        var classifier = With(Rule("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.Equal("Halo", classifier.Classify("msedge", "anything", "halo.lutz.us/tickets").Category);
        Assert.Equal("Halo", classifier.Classify("chrome", "anything", "halo.lutz.us/config/email").Category);
    }

    [Fact]
    public void APattern_MatchesTheTitleTooWhenThereIsNoPage()
    {
        var classifier = With(Rule("halo", @"Halo\s?PSA", "Halo"));

        Assert.Equal("Halo", classifier.Classify("msedge", "HaloPSA — tickets", url: null).Category);
    }

    [Fact]
    public void EitherHalfMatching_IsEnough()
    {
        var classifier = With(Rule("halo", @"^halo\.lutz\.us", "Halo"));

        // The page says so and the title doesn't — which is the case that matters, because a
        // browser's title lags a tab switch while its address bar does not.
        Assert.Equal("Halo", classifier.Classify("msedge", "Inbox — Outlook", "halo.lutz.us/tickets").Category);
    }

    [Fact]
    public void AWindowThatIsNeither_StaysUnclassified()
    {
        var classifier = With(Rule("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.True(classifier.Classify("explorer", "Downloads", url: null).IsUnclassified);
        Assert.True(classifier.Classify("msedge", "Analytics", "example.com/x").IsUnclassified);
    }

    [Fact]
    public void APatternWrittenForAPage_CanAlsoMatchATitleThatReadsLikeOne()
    {
        // The cost of reading both with one pattern, stated plainly: an address-shaped pattern is
        // no longer confined to addresses. It is a fair trade — a window titled after the site is
        // almost always that site's work — but it is a real widening, so it is pinned here rather
        // than left to be discovered.
        var classifier = With(Rule("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.Equal("Halo", classifier.Classify("explorer", "halo.lutz.us backup", url: null).Category);
    }

    [Fact]
    public void APageRule_DoesNotMatchTheSiteNameAppearingMidAddress()
    {
        var classifier = With(Rule("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.True(classifier.Classify("msedge", "t", "example.com/halo.lutz.us").IsUnclassified);
    }

    [Fact]
    public void TheAppPatternIsStillAConjunction_TheMatchPatternIsTheAlternation()
    {
        var rule = new ClassificationRule
        {
            Id = "r", ProcessPattern = "^msedge$", MatchPattern = @"^halo\.lutz\.us", Category = "Halo",
        };

        Assert.Equal("Halo", With(rule).Classify("msedge", "Ticket 5", "halo.lutz.us/ticket").Category);
        // Wrong app: the app pattern still has to hold whatever the page says.
        Assert.True(With(rule).Classify("chrome", "Ticket 5", "halo.lutz.us/ticket").IsUnclassified);
        // Right app, but neither title nor page matches.
        Assert.True(With(rule).Classify("msedge", "Inbox", "example.com/ticket").IsUnclassified);
    }

    // ---- what a pattern can extract ----

    [Fact]
    public void APagePattern_CanNameTheTicket()
    {
        // GitHub puts the number in the path, so unlike Halo it can be read straight off.
        var classifier = With(Rule("gh", @"^github\.com/[^/]+/[^/]+/issues/(?<ticket>\d+)", "Development"));

        Assert.Equal("2719", classifier.Classify("msedge", "whatever", "github.com/lutz-tech/ATT/issues/2719").TicketRef);
    }

    [Fact]
    public void APagePattern_CanNameTheClientAndSubject()
    {
        var classifier = With(
            Rule("ig", @"^lutz\.itglue\.com/(?<client>\d+)/(?<subject>[a-z]+)", "IT Glue"));

        var classification = classifier.Classify("msedge", "t", "lutz.itglue.com/9195837/passwords");
        Assert.Equal("9195837", classification.Client);
        Assert.Equal("passwords", classification.Subject);
    }

    [Fact]
    public void WhenBothCouldMatch_TheTitlesCapturesWin()
    {
        // The title is tried first because it is the more specific evidence — a ticket number
        // written into a window title names the ticket being worked, where a path might be a list.
        var classifier = With(Rule("halo", @"Ticket (?<ticket>\d+)", "Halo"));

        var classification = classifier.Classify("msedge", "Ticket 495308 (Acme)", "app.example.com/Ticket 11");
        Assert.Equal("495308", classification.TicketRef);
    }

    // ---- drafting one from the Uncategorized tab ----

    [Fact]
    public void ASiteRule_KeysOnTheHostAlone_SoAnyBrowserCounts()
    {
        var rule = RuleDraft.Create(
            "msedge", "Passwords — IT Glue", RuleMatch.Site, "IT Glue", null, url: "lutz.itglue.com/9195837/passwords");

        Assert.Null(rule.ProcessPattern);
        Assert.Equal(@"^lutz\.itglue\.com(?:/|$)", rule.MatchPattern);

        // And it does what it says on every page of that site, in either browser.
        var classifier = With(rule);
        Assert.Equal("IT Glue", classifier.Classify("chrome", "anything", "lutz.itglue.com/organizations").Category);
        Assert.Equal("IT Glue", classifier.Classify("msedge", "anything", "lutz.itglue.com").Category);
    }

    [Fact]
    public void ASiteRule_DoesNotSwallowASiteThatMerelyStartsTheSameWay()
    {
        var rule = RuleDraft.Create("msedge", "t", RuleMatch.Site, "Halo", null, url: "halo.lutz.us/tickets");

        Assert.True(With(rule).Classify("msedge", "t", "halo.lutz.us.evil.com/x").IsUnclassified);
    }

    [Fact]
    public void ASiteRule_NeedsAPage()
        => Assert.Throws<ArgumentException>(() =>
            RuleDraft.Create("notepad", "untitled", RuleMatch.Site, "Admin", null, url: null));

    [Theory]
    [InlineData("halo.lutz.us/tickets", "halo.lutz.us")]
    [InlineData("halo.lutz.us", "halo.lutz.us")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheHostIsTheAddressUpToTheFirstSlash(string? url, string? expected)
        => Assert.Equal(expected, RuleDraft.HostOf(url));

    // ---- the file ----

    [Fact]
    public void ARule_RoundTripsThroughTheRulesFile()
    {
        var json = RulesFile.WithRule("""{ "rules": [] }""", Rule("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.Contains("\"matchPattern\"", json);
        Assert.Equal(@"^halo\.lutz\.us", Assert.Single(RulesFile.Parse(json)).MatchPattern);
    }

    [Fact]
    public void AFileWrittenBeforeTheMerge_StillLoads()
    {
        // The two old keys each read as the one pattern a rule now has, so nobody's rules file
        // stops working — a failed load would read as no rules at all.
        const string old =
            """
            {
              "rules": [
                { "id": "a", "titlePattern": "Alpha", "category": "A" },
                { "id": "b", "urlPattern": "^b\\.com", "category": "B" }
              ]
            }
            """;

        var rules = RulesFile.Parse(old);

        Assert.Equal("Alpha", rules[0].MatchPattern);
        Assert.Equal(@"^b\.com", rules[1].MatchPattern);
    }

    [Fact]
    public void AnOldRuleCarryingBothPatterns_BecomesTheAlternationOfThem()
    {
        // It used to need both. Nothing can say that now, so it is widened rather than narrowed:
        // the activity stays classified instead of falling back to Uncategorized.
        const string old =
            """
            {
              "rules": [
                { "id": "a", "titlePattern": "Ticket", "urlPattern": "^halo\\.lutz\\.us", "category": "Halo" }
              ]
            }
            """;

        var rule = Assert.Single(RulesFile.Parse(old));

        Assert.Equal(@"(?:Ticket)|(?:^halo\.lutz\.us)", rule.MatchPattern);
        Assert.Equal("Halo", With(rule).Classify("msedge", "Ticket 5", url: null).Category);
        Assert.Equal("Halo", With(rule).Classify("msedge", "anything", "halo.lutz.us/x").Category);
    }

    [Fact]
    public void ASiteRule_LandsAfterTheOtherMatchRules_SoTicketExtractionStillWins()
    {
        // A Halo site rule placed on top would outrank the rule that reads the ticket number, and
        // the numbers would quietly stop coming through.
        const string existing =
            """
            {
              "rules": [
                { "id": "halo-ticket", "matchPattern": "Ticket (?<ticket>\\d+)", "category": "Halo" },
                { "id": "outlook-app", "processPattern": "^OUTLOOK$", "category": "Outlook" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Rule("halo-site", @"^halo\.lutz\.us", "Halo"), RulePlacement.Site);
        var ids = RulesFile.Parse(json).Select(r => r.Id).ToList();

        Assert.Equal(["halo-ticket", "halo-site", "outlook-app"], ids);

        // And the order means what it should: the ticket still gets extracted.
        var classifier = new Classifier(RulesFile.Parse(json));
        Assert.Equal("495308", classifier.Classify("msedge", "Ticket 495308", "halo.lutz.us/ticket").TicketRef);
    }

    [Fact]
    public void WithNoMatchRulesToFollow_ASiteRuleJustAppends()
    {
        const string existing =
            """
            {
              "rules": [
                { "id": "outlook-app", "processPattern": "^OUTLOOK$", "category": "Outlook" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Rule("halo-site", @"^halo\.lutz\.us", "Halo"), RulePlacement.Site);

        Assert.Equal(["outlook-app", "halo-site"], RulesFile.Parse(json).Select(r => r.Id));
    }

    [Fact]
    public void AddingASiteRule_LeavesTheFilesCommentsAlone()
    {
        const string existing =
            """
            {
              // header comment
              "rules": [
                { "id": "a", "matchPattern": "Alpha", "category": "A" },
                // note about b
                { "id": "b", "processPattern": "^b$", "category": "B" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Rule("c", @"^c\.com", "C"), RulePlacement.Site);

        Assert.Contains("// header comment", json);
        Assert.Contains("// note about b", json);
        Assert.Equal(["a", "c", "b"], RulesFile.Parse(json).Select(r => r.Id));
    }
}
