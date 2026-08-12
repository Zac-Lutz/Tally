using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class ReportWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Date = new(2026, 8, 12);

    private static ClassifiedBlock CB(
        double startMinutes, double endMinutes, string category, string title,
        string? client = null, string? ticket = null, string process = "chrome")
        => new(
            new Block(T0.AddMinutes(startMinutes), T0.AddMinutes(endMinutes), process, title),
            new Classification(category, client, ticket, category == Classification.Unclassified ? null : "rule"));

    [Fact]
    public void Rollup_GroupsByCategoryClientTicket_AndOrdersByDuration()
    {
        var md = ReportWriter.BuildMarkdown(Date,
        [
            CB(0, 30, "HaloPSA", "Ticket #1 - HaloPSA", ticket: "1"),
            CB(30, 80, "Email", "Inbox - Outlook"),
            CB(80, 110, "HaloPSA", "Ticket #1 again - HaloPSA", ticket: "1"),
        ], [], []);

        var rollupLines = md.Split('\n')
            .Where(l => l.StartsWith("| HaloPSA") || l.StartsWith("| Email"))
            .ToList();

        Assert.Equal(2, rollupLines.Count);
        Assert.Contains("HaloPSA", rollupLines[0]);   // 60m HaloPSA sorts above 50m Email
        Assert.Contains("#1", rollupLines[0]);
        Assert.Contains("1h 00m", rollupLines[0]);
        Assert.Contains("Email", rollupLines[1]);
        Assert.Contains("50m", rollupLines[1]);
    }

    [Fact]
    public void VerbatimTitles_AppearInTimeline_WithPipesEscaped()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 30, "Teams", "Standup | Microsoft Teams", process: "ms-teams")], [], []);

        Assert.Contains("Standup \\| Microsoft Teams", md);
    }

    [Fact]
    public void GapsSection_ListsLongIdle_AndLongUnclassifiedBlocks()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 60, Classification.Unclassified, "mystery window")],
            [],
            [new InactivePeriod(T0.AddMinutes(60), T0.AddMinutes(106), InactiveReasons.Idle)]);

        Assert.Contains("## Gaps to account for", md);
        Assert.Contains("mystery window", md);
        Assert.Contains("idle (46m)", md);
    }

    [Fact]
    public void ShortIdle_IsNotListedAsAGap()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 60, "Email", "Inbox - Outlook")],
            [],
            [new InactivePeriod(T0.AddMinutes(60), T0.AddMinutes(62), InactiveReasons.Idle)]);

        Assert.DoesNotContain("## Gaps to account for", md);
    }

    [Fact]
    public void CallsSection_ListsCallsWithDurations()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 30, "Teams", "t", process: "ms-teams")],
            [new CallSpan(T0, T0.AddMinutes(32), "ms-teams", "Weekly standup | Microsoft Teams")],
            []);

        Assert.Contains("## Calls", md);
        Assert.Contains("Weekly standup", md);
        Assert.Contains("32m", md);
    }

    [Fact]
    public void EmptyDay_SaysSo()
    {
        var md = ReportWriter.BuildMarkdown(Date, [], [], []);

        Assert.Contains("No activity recorded.", md);
    }
}
