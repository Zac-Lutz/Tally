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
            new Classification(category, null, ticket, subject, "rule"),
            BlockActivity.None);

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
}
