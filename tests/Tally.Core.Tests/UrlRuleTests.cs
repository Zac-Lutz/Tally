using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// Rules that match the page a browser was showing. A tab's title changes constantly while its
/// address stays put, so the site is often the truer thing to key a rule on.
/// </summary>
public class UrlRuleTests
{
    private static Classifier With(params ClassificationRule[] rules) => new(rules);

    private static ClassificationRule Site(string id, string pattern, string category)
        => new() { Id = id, UrlPattern = pattern, Category = category };

    // ---- matching ----

    [Fact]
    public void APageRule_MatchesEveryPageOnThatSite()
    {
        var classifier = With(Site("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.Equal("Halo", classifier.Classify("msedge", "anything", "halo.lutz.us/tickets").Category);
        Assert.Equal("Halo", classifier.Classify("chrome", "anything", "halo.lutz.us/config/email").Category);
    }

    [Fact]
    public void APageRule_IgnoresAnythingWithNoPage()
    {
        // Nothing for the pattern to be true of — a rule about a website must not match Explorer.
        var classifier = With(Site("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.True(classifier.Classify("explorer", "halo.lutz.us backup", url: null).IsUnclassified);
    }

    [Fact]
    public void APageRule_DoesNotMatchTheSiteNameAppearingMidAddress()
    {
        var classifier = With(Site("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.True(classifier.Classify("msedge", "t", "example.com/halo.lutz.us").IsUnclassified);
    }

    [Fact]
    public void EveryPatternARuleCarries_HasToMatch()
    {
        // App, window and page are one conjunction, the way app and window already were.
        var rule = new ClassificationRule
        {
            Id = "r", ProcessPattern = "^msedge$", TitlePattern = "Ticket", UrlPattern = @"^halo\.lutz\.us",
            Category = "Halo",
        };

        Assert.Equal("Halo", With(rule).Classify("msedge", "Ticket 5", "halo.lutz.us/ticket").Category);
        Assert.True(With(rule).Classify("chrome", "Ticket 5", "halo.lutz.us/ticket").IsUnclassified);
        Assert.True(With(rule).Classify("msedge", "Inbox", "halo.lutz.us/ticket").IsUnclassified);
        Assert.True(With(rule).Classify("msedge", "Ticket 5", "example.com/ticket").IsUnclassified);
    }

    // ---- what a page rule can extract ----

    [Fact]
    public void APagePattern_CanNameTheTicket()
    {
        // GitHub puts the number in the path, so unlike Halo it can be read straight off.
        var classifier = With(Site("gh", @"^github\.com/[^/]+/[^/]+/issues/(?<ticket>\d+)", "Development"));

        Assert.Equal("2719", classifier.Classify("msedge", "whatever", "github.com/lutz-tech/ATT/issues/2719").TicketRef);
    }

    [Fact]
    public void APagePattern_CanNameTheClientAndSubject()
    {
        var classifier = With(new ClassificationRule
        {
            Id = "ig",
            UrlPattern = @"^lutz\.itglue\.com/(?<client>\d+)/(?<subject>[a-z]+)",
            Category = "IT Glue",
        });

        var classification = classifier.Classify("msedge", "t", "lutz.itglue.com/9195837/passwords");
        Assert.Equal("9195837", classification.Client);
        Assert.Equal("passwords", classification.Subject);
    }

    [Fact]
    public void WhenARuleReadsBoth_TheWindowsCapturesWin_AndThePageFillsTheGaps()
    {
        var classifier = With(new ClassificationRule
        {
            Id = "both",
            TitlePattern = @"Ticket (?<ticket>\d+)",
            UrlPattern = @"^halo\.lutz\.us/(?<subject>\w+)",
            Category = "Halo",
        });

        var classification = classifier.Classify("msedge", "Ticket 495308 (Acme)", "halo.lutz.us/ticket");
        Assert.Equal("495308", classification.TicketRef);   // the title's, being the more specific
        Assert.Equal("ticket", classification.Subject);     // the page's, the title having none
    }

    // ---- drafting one from the Uncategorized tab ----

    [Fact]
    public void ASiteRule_KeysOnTheHostAlone_SoAnyBrowserCounts()
    {
        var rule = RuleDraft.Create(
            "msedge", "Passwords — IT Glue", RuleMatch.Site, "IT Glue", null, url: "lutz.itglue.com/9195837/passwords");

        Assert.Null(rule.ProcessPattern);
        Assert.Null(rule.TitlePattern);
        Assert.Equal(@"^lutz\.itglue\.com(?:/|$)", rule.UrlPattern);

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
    public void APageRule_RoundTripsThroughTheRulesFile()
    {
        var json = RulesFile.WithRule(
            """{ "rules": [] }""",
            Site("halo", @"^halo\.lutz\.us", "Halo"));

        Assert.Contains("\"urlPattern\"", json);
        Assert.Equal(@"^halo\.lutz\.us", Assert.Single(RulesFile.Parse(json)).UrlPattern);
    }

    [Fact]
    public void APageRule_LandsAfterTheWindowRules_SoTicketExtractionStillWins()
    {
        // A Halo site rule placed on top would outrank the title rule that reads the ticket
        // number, and the numbers would quietly stop coming through.
        const string existing =
            """
            {
              "rules": [
                { "id": "halo-ticket", "titlePattern": "Ticket (?<ticket>\\d+)", "category": "Halo" },
                { "id": "outlook-app", "processPattern": "^OUTLOOK$", "category": "Outlook" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Site("halo-site", @"^halo\.lutz\.us", "Halo"));
        var ids = RulesFile.Parse(json).Select(r => r.Id).ToList();

        Assert.Equal(["halo-ticket", "halo-site", "outlook-app"], ids);

        // And the order means what it should: the ticket still gets extracted.
        var classifier = new Classifier(RulesFile.Parse(json));
        Assert.Equal("495308", classifier.Classify("msedge", "Ticket 495308", "halo.lutz.us/ticket").TicketRef);
    }

    [Fact]
    public void WithNoWindowRulesToFollow_APageRuleJustAppends()
    {
        const string existing =
            """
            {
              "rules": [
                { "id": "outlook-app", "processPattern": "^OUTLOOK$", "category": "Outlook" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Site("halo-site", @"^halo\.lutz\.us", "Halo"));

        Assert.Equal(["outlook-app", "halo-site"], RulesFile.Parse(json).Select(r => r.Id));
    }

    [Fact]
    public void AddingAPageRule_LeavesTheFilesCommentsAlone()
    {
        const string existing =
            """
            {
              // header comment
              "rules": [
                { "id": "a", "titlePattern": "Alpha", "category": "A" },
                // note about b
                { "id": "b", "processPattern": "^b$", "category": "B" }
              ]
            }
            """;

        var json = RulesFile.WithRule(existing, Site("c", @"^c\.com", "C"));

        Assert.Contains("// header comment", json);
        Assert.Contains("// note about b", json);
        Assert.Equal(["a", "c", "b"], RulesFile.Parse(json).Select(r => r.Id));
    }
}
