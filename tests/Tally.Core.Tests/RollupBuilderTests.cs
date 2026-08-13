using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class RollupBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock CB(
        double startMin, double endMin, string category, string title,
        string? ticket = null, string? subject = null)
        => new(
            new Block(T0.AddMinutes(startMin), T0.AddMinutes(endMin), "proc", title),
            new Classification(category, null, ticket, subject, "rule"));

    [Fact]
    public void DistinctBrowserTabs_EachBecomeTheirOwnRow()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "Browsing", "Ticket A page - Work - Microsoft​ Edge"),
            CB(10, 15, "Browsing", "Ticket B page - Work - Microsoft​ Edge"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.DetailName == "Ticket A page - Work");
        Assert.Contains(rows, r => r.DetailName == "Ticket B page - Work");
    }

    [Fact]
    public void SameTab_RevisitedAndAtDifferentTabCounts_RollsUpToOneRow()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "Browsing", "Design doc and 7 more pages - Work - Microsoft​ Edge"),
            CB(30, 45, "Browsing", "Design doc and 2 more pages - Work - Microsoft​ Edge"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Design doc - Work", row.DetailName);
        Assert.Equal(TimeSpan.FromMinutes(25), row.Time);   // 10 + 15
    }

    [Fact]
    public void DifferentHaloTickets_AreSeparateRows()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "HaloPSA", "12345 - VPN drops", ticket: "12345"),
            CB(10, 20, "HaloPSA", "67890 - Printer offline", ticket: "67890"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TicketRef == "12345");
        Assert.Contains(rows, r => r.TicketRef == "67890");
    }

    [Fact]
    public void SameTicket_WithSlightlyDifferentTitles_MergesByTicket()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "HaloPSA", "12345 - VPN drops", ticket: "12345"),
            CB(20, 25, "HaloPSA", "12345 - VPN drops (in progress)", ticket: "12345"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("12345", row.TicketRef);
        Assert.Equal(TimeSpan.FromMinutes(15), row.Time);
    }

    [Fact]
    public void TeamsChats_SeparateBySubject()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "Teams", "Chat | Matt | Microsoft Teams", subject: "Matt"),
            CB(10, 16, "Teams", "Chat | Service Family | Microsoft Teams", subject: "Service Family"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.DetailName == "Matt");
        Assert.Contains(rows, r => r.DetailName == "Service Family");
    }

    [Fact]
    public void RowsAreOrderedByTimeDescending()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 5, "Browsing", "Short tab - Work - Microsoft​ Edge"),
            CB(5, 45, "Development", "Big task - Visual Studio"),
        ]);

        Assert.Equal("Big task - Visual Studio", rows[0].DetailName);   // 40m first
    }

    private static CallSpan Call(double startMin, double endMin, string process, string title = "")
        => new(T0.AddMinutes(startMin), T0.AddMinutes(endMin), process, title);

    [Fact]
    public void BuildCalls_SumsSameAppAcrossTheDay_UnderTheCallCategory()
    {
        var rows = RollupBuilder.BuildCalls(
        [
            Call(0, 20, "Discord"),
            Call(120, 145, "Discord"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Call", row.Category);
        Assert.Equal("Discord", row.DetailName);
        Assert.Equal(TimeSpan.FromMinutes(45), row.Time);   // 20 + 25
    }

    [Fact]
    public void BuildCalls_KeepsTheWindowTitleWhenItAddsDetail()
    {
        var rows = RollupBuilder.BuildCalls([Call(0, 30, "ms-teams", "Standup - Microsoft Teams")]);

        var row = Assert.Single(rows);
        Assert.Equal("ms-teams", row.Client);                       // the app
        Assert.Equal("Standup - Microsoft Teams", row.DetailName);  // ... plus what it was about
    }

    [Fact]
    public void BuildCalls_DropsARedundantTitleThatJustRepeatsTheApp()
    {
        var rows = RollupBuilder.BuildCalls([Call(0, 30, "Discord", "Discord")]);

        var row = Assert.Single(rows);
        Assert.Null(row.Client);              // no "Discord / Discord"
        Assert.Equal("Discord", row.DetailName);
    }

    [Fact]
    public void BuildCalls_DistinctChannelsAreSeparateRows()
    {
        // Looking ahead to Discord channel detection: different call subjects stay separate.
        var rows = RollupBuilder.BuildCalls(
        [
            Call(0, 15, "Discord", "General"),
            Call(20, 35, "Discord", "Dev Team"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.DetailName == "General");
        Assert.Contains(rows, r => r.DetailName == "Dev Team");
    }

    [Fact]
    public void BuildTimers_SummarizesByName_UnderTheTimerCategory()
    {
        var rows = RollupBuilder.BuildTimers(
        [
            new ManualTimer { Name = "Ticket 123", Start = T0, End = T0.AddMinutes(20) },
            new ManualTimer { Name = "Ticket 123", Start = T0.AddHours(1), End = T0.AddHours(1).AddMinutes(10) },
            new ManualTimer { Name = "Standup", Start = T0.AddHours(2), End = T0.AddHours(2).AddMinutes(15) },
        ]);

        Assert.Equal(2, rows.Count);
        var ticket = Assert.Single(rows, r => r.DetailName == "Ticket 123");
        Assert.Equal("Timer", ticket.Category);
        Assert.Equal(TimeSpan.FromMinutes(30), ticket.Time);   // 20 + 10, summed by name
        Assert.Null(ticket.RowKey);                            // not ticket-editable in the rollup
    }
}
