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
