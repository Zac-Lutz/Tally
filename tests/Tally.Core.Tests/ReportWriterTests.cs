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
        string? client = null, string? ticket = null, string? subject = null,
        int keys = 0, int clicks = 0, string process = "chrome")
        => new(
            new Block(T0.AddMinutes(startMinutes), T0.AddMinutes(endMinutes), process, title),
            new Classification(category, client, ticket, subject, category == Classification.Unclassified ? null : "rule"),
            keys == 0 && clicks == 0 ? BlockActivity.None : new BlockActivity(keys, clicks));

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

    [Fact]
    public void Clock_IsRendered12Hour_LowercaseMeridiem()
    {
        // Build the block at the machine's local offset so ToLocalTime is a no-op and the
        // rendered wall-clock is timezone-independent (2:30pm-3:00pm regardless of runner TZ).
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 12, 14, 30, 0));
        var start = new DateTimeOffset(2026, 8, 12, 14, 30, 0, localOffset);
        var block = new ClassifiedBlock(
            new Block(start, start.AddMinutes(30), "chrome", "x"),
            new Classification("Browsing", null, null, null, "rule"),
            BlockActivity.None);

        var md = ReportWriter.BuildMarkdown(Date, [block], [], []);

        Assert.Contains("2:30pm", md);
        Assert.Contains("3:00pm", md);
        Assert.DoesNotContain("14:30", md);   // no 24-hour clock survives
    }

    [Fact]
    public void Rollup_SeparatesTeamsChats_BySubject()
    {
        var md = ReportWriter.BuildMarkdown(Date,
        [
            CB(0, 15, "Teams", "Chat | Matt Longenecker | Microsoft Teams", subject: "Matt Longenecker", process: "ms-teams"),
            CB(15, 21, "Teams", "Chat | Service Family | Microsoft Teams", subject: "Service Family", process: "ms-teams"),
        ], [], []);

        var rollup = md.Split('\n').Where(l => l.StartsWith("| Teams")).ToList();
        Assert.Equal(2, rollup.Count);
        Assert.Contains(rollup, l => l.Contains("Matt Longenecker"));
        Assert.Contains(rollup, l => l.Contains("Service Family"));
    }

    [Fact]
    public void Activity_AppearsInSummaryAndRollup()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 30, "HaloPSA", "Ticket #1 - HaloPSA", ticket: "1", keys: 412, clicks: 88)], [], []);

        Assert.Contains("412 keys", md);   // summary line
        Assert.Contains("88 clicks", md);
        Assert.Contains("412/88", md);     // rollup activity cell
    }

    [Fact]
    public void ZeroActivityBlock_RendersDash()
    {
        var md = ReportWriter.BuildMarkdown(Date,
            [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        var timeline = md.Split('\n').First(l => l.Contains("Inbox - Outlook"));
        Assert.Contains("—", timeline);
    }
}
