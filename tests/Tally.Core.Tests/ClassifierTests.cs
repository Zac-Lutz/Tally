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

        Assert.Equal("Halo", c.Category);
        Assert.Equal("12345", c.TicketRef);
    }

    // Halo's web app titles are unbranded breadcrumbs; when a ticket is open its number is the
    // last segment. Real titles from a captured day, browser suffix included.
    [Fact]
    public void HaloBreadcrumbTab_WithTrailingNumber_ExtractsTheTicket()
    {
        var c = DefaultClassifier().Classify(
            "msedge", "Tickets > Management > Zac Franklin > 493876 and 5 more pages - Work - Microsoft​ Edge");

        Assert.Equal("Halo", c.Category);
        Assert.Equal("493876", c.TicketRef);
        Assert.Equal("halo-ticket-tab", c.RuleId);
    }

    [Theory]
    [InlineData("Tickets > Management > Zac Franklin and 5 more pages - Work - Microsoft​ Edge")]
    [InlineData("Tickets > Management > 🛑 Missing Category and 4 more pages - Work - Microsoft​ Edge")]
    [InlineData("Configuration > Tickets > Ticket Types and 11 more pages - Work - Microsoft​ Edge")]
    [InlineData("Configuration > Integrations > Custom Integrations > Methods and 11 more pages - Work - Microsoft​ Edge")]
    public void HaloBreadcrumbTab_WithoutATicket_IsStillHalo(string title)
    {
        var c = DefaultClassifier().Classify("msedge", title);

        Assert.Equal("Halo", c.Category);
        Assert.Null(c.TicketRef);
    }

    [Theory]
    [InlineData("Organizations — IT Glue and 6 more pages - Work - Microsoft​ Edge")]
    [InlineData("Pella Windows and Doors — IT Glue and 10 more pages - Work - Microsoft​ Edge")]
    [InlineData("IT Glue and 5 more pages - Work - Microsoft​ Edge")]
    public void ItGlueTab_IsItGlue(string title)
        => Assert.Equal("IT Glue", DefaultClassifier().Classify("msedge", title).Category);

    [Theory]
    [InlineData("msedge", "Mail - Zac Franklin - Outlook and 4 more pages - Work - Microsoft​ Edge")]
    [InlineData("msedge", "Calendar - Zac Franklin - Outlook and 5 more pages - Work - Microsoft​ Edge")]
    [InlineData("msedge", "Outlook and 7 more pages - Work - Microsoft​ Edge")]
    public void OwaTab_IsOutlook(string process, string title)
        => Assert.Equal("Outlook", DefaultClassifier().Classify(process, title).Category);

    // The desktop app is claimed by process name — olk is new Outlook, outlook is classic —
    // whatever the window title says.
    [Theory]
    [InlineData("olk")]
    [InlineData("OUTLOOK")]
    public void OutlookDesktopApp_IsOutlook_ByProcess(string process)
    {
        var c = DefaultClassifier().Classify(process, "Inbox - kirra@example.com");

        Assert.Equal("Outlook", c.Category);
        Assert.Equal("outlook-app", c.RuleId);
    }

    [Fact]
    public void PageMerelyMentioningOutlook_IsNotClaimed()
        => Assert.True(DefaultClassifier()
            .Classify("msedge", "what's new in Outlook - Google Search and 3 more pages - Work - Microsoft​ Edge")
            .IsUnclassified);

    [Fact]
    public void UnmatchedBrowserTab_IsUnclassified_NotBrowsing()
    {
        // No catch-all browser rule: unknown tabs surface in Unclassified to be taught a rule.
        var c = DefaultClassifier().Classify(
            "msedge", "Pull requests · lutz-tech/ATT and 5 more pages - Work - Microsoft​ Edge");

        Assert.True(c.IsUnclassified);
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

    // RingCentral has shipped under several executable names, so the rule matches the prefix.
    [Theory]
    [InlineData("RingCentral")]
    [InlineData("RingCentralPhone")]
    [InlineData("RingCentralMeetings")]
    public void RingCentral_IsMatchedWhicheverClientIsInstalled(string process)
        => Assert.Equal("RingCentral", DefaultClassifier().Classify(process, "RingCentral").Category);

    [Fact]
    public void RingCentral_DoesNotClaimABrowserTabAboutIt()
        => Assert.True(DefaultClassifier()
            .Classify("msedge", "ringcentral - Google Search - Work").IsUnclassified);

    [Fact]
    public void Discord_DoesNotClaimOtherAppsThatMentionIt()
    {
        // A shell jump list and a browser tab both say "Discord"; only the app itself counts.
        Assert.True(DefaultClassifier().Classify("ShellExperienceHost", "Jump List for Discord").IsUnclassified);
        Assert.True(DefaultClassifier().Classify("chrome", "Discord | Lutz Tech").IsUnclassified);
    }

    [Fact]
    public void ScreenConnect_ClientIsCapturedFromTitle()
    {
        var c = DefaultClassifier().Classify("chrome", "Acme Corp - Backstage - ScreenConnect");

        Assert.Equal("ScreenConnect", c.Category);
        Assert.Equal("Acme Corp", c.Client);
    }

    [Fact]
    public void FirstMatchWins_TicketRuleBeatsTheBrandRule()
    {
        // The title matches both the ticket rule and the HaloPSA brand rule; the ticket rule is
        // earlier in the list, so the number is captured.
        var c = DefaultClassifier().Classify("chrome", "Ticket #777 - HaloPSA");

        Assert.Equal("Halo", c.Category);
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
        Assert.True(c.IsUnclassified);
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

        Assert.Equal("Teams - Chat", c.Category);   // reads apart from "Teams - Call" on a timesheet
        Assert.Equal("Matt Longenecker", c.Subject);
        Assert.Equal("teams-chat", c.RuleId);
    }

    [Fact]
    public void TeamsWindow_WithNoConversationInTheTitle_StaysPlainTeams()
    {
        // Neither a chat nor a call — the activity feed, the calendar, a settings pane.
        var c = DefaultClassifier().Classify("ms-teams", "Microsoft Teams");

        Assert.Equal("Teams", c.Category);
        Assert.Equal("teams", c.RuleId);
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
