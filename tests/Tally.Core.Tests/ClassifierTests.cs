using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class ClassifierTests
{
    /// <summary>Builds a classifier from the shipped starter rules, round-tripped through RulesFile.</summary>
    private static Classifier DefaultClassifier()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, RulesFile.DefaultRulesJson);
            return new Classifier(RulesFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HaloTicketNumber_IsExtracted()
    {
        var c = DefaultClassifier().Classify("chrome", "Ticket #12345 - VPN drops at Acme - HaloPSA");

        Assert.Equal("HaloPSA", c.Category);
        Assert.Equal("12345", c.TicketRef);
    }

    // The four title shapes Discord actually produces, taken from a real day's captures.
    [Theory]
    [InlineData("#type-here-just-no-client-info-eh | Lutz Tech - Discord", "#type-here-just-no-client-info-eh | Lutz Tech")]
    [InlineData("@zyr - Discord", "@zyr")]
    [InlineData("Friends - Discord", "Friends")]
    public void Discord_ChannelIsCapturedAsTheSubject(string title, string expected)
    {
        var c = DefaultClassifier().Classify("Discord", title);

        Assert.Equal("Discord", c.Category);
        Assert.Equal(expected, c.Subject);
    }

    [Theory]
    [InlineData("Discord")]
    [InlineData("")]
    public void Discord_WithNoChannelInTheTitle_IsStillDiscord(string title)
    {
        var c = DefaultClassifier().Classify("Discord", title);

        Assert.Equal("Discord", c.Category);
        Assert.Null(c.Subject);
    }

    [Fact]
    public void Discord_DoesNotClaimOtherAppsThatMentionIt()
    {
        // A shell jump list and a browser tab both say "Discord"; only the app itself counts.
        Assert.True(DefaultClassifier().Classify("ShellExperienceHost", "Jump List for Discord").IsUnclassified);
        Assert.Equal("Browsing", DefaultClassifier().Classify("chrome", "Discord | Lutz Tech").Category);
    }

    [Fact]
    public void ScreenConnect_ClientIsCapturedFromTitle()
    {
        var c = DefaultClassifier().Classify("chrome", "Acme Corp - Backstage - ScreenConnect");

        Assert.Equal("Remote Support", c.Category);
        Assert.Equal("Acme Corp", c.Client);
    }

    [Fact]
    public void FirstMatchWins_TicketRuleBeatsGenericBrowserRule()
    {
        // chrome also matches the catch-all browser rule; the ticket rule is earlier in the list.
        var c = DefaultClassifier().Classify("chrome", "Ticket #777 - HaloPSA");

        Assert.Equal("HaloPSA", c.Category);
        Assert.Equal("halo-ticket", c.RuleId);
    }

    [Fact]
    public void UnknownProcessAndTitle_IsUnclassified()
    {
        var c = DefaultClassifier().Classify("notepad", "untitled - Notepad");

        Assert.True(c.IsUnclassified);
        Assert.Null(c.RuleId);
    }

    [Fact]
    public void ProcessRule_DoesNotMatchOtherProcesses()
    {
        var c = DefaultClassifier().Classify("chrome", "some page");

        Assert.NotEqual("Teams", c.Category);   // the teams process rule must not fire for chrome
        Assert.Equal("Browsing", c.Category);
    }

    [Fact]
    public void StaticClientOnRule_IsApplied_WhenTitleHasNoCaptureGroup()
    {
        var classifier = new Classifier(
        [
            new ClassificationRule
            {
                Id = "acme-vpn",
                TitlePattern = "AnyConnect",
                Category = "Remote Support",
                Client = "Acme Corp",
            },
        ]);

        var c = classifier.Classify("vpnui", "Cisco AnyConnect");

        Assert.Equal("Acme Corp", c.Client);
    }

    [Fact]
    public void TeamsChatName_IsExtractedAsSubject()
    {
        var c = DefaultClassifier().Classify("ms-teams", "Chat | Matt Longenecker | Microsoft Teams");

        Assert.Equal("Teams", c.Category);
        Assert.Equal("Matt Longenecker", c.Subject);
        Assert.Equal("teams-chat", c.RuleId);
    }

    [Fact]
    public void TeamsChannel_SubjectKeepsTeamAndChannel()
    {
        var c = DefaultClassifier().Classify("ms-teams", "Chat | Lutz Tech | att Dev Channel | Microsoft Teams");

        Assert.Equal("Lutz Tech | att Dev Channel", c.Subject);
    }

    [Fact]
    public void TeamsTitle_WithoutChatPrefix_StillExtractsSubject()
    {
        var c = DefaultClassifier().Classify("ms-teams", "All Hands Content Gathering | Microsoft Teams");

        Assert.Equal("All Hands Content Gathering", c.Subject);
    }

    [Fact]
    public void BareTeamsWindow_ClassifiesAsTeams_WithNoSubject()
    {
        // No "| Microsoft Teams"-preceded name → the chat rule can't match; falls to the generic rule.
        var c = DefaultClassifier().Classify("ms-teams", "Microsoft Teams");

        Assert.Equal("Teams", c.Category);
        Assert.Null(c.Subject);
        Assert.Equal("teams", c.RuleId);
    }

    [Fact]
    public void RuleWithNoPatterns_NeverMatches()
    {
        // A pattern-less rule would otherwise swallow everything ahead of later rules.
        var classifier = new Classifier(
        [
            new ClassificationRule { Id = "inert", Category = "Everything" },
        ]);

        Assert.True(classifier.Classify("chrome", "whatever").IsUnclassified);
    }
}
