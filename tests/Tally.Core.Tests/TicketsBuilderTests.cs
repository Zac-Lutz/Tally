using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>The Tickets tab's grouping: the day's blocks, one row per effective ticket.</summary>
public class TicketsBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock CB(
        double startMin, double endMin, string title, string? ticket = null,
        string process = "msedge", string category = "Halo", string? overrideTicket = null)
        => new(
            new Block(T0.AddMinutes(startMin), T0.AddMinutes(endMin), process, title),
            new Classification(category, null, ticket, null, "rule"),
            overrideTicket);

    [Fact]
    public void ATicketWorkedAcrossAppsAndWindows_IsOneRow()
    {
        var rows = TicketsBuilder.Build(
        [
            CB(0, 10, "Tickets > Management > 42", ticket: "42"),
            CB(30, 40, "RE: ticket 42 - Outlook", ticket: "42", process: "olk", category: "Outlook"),
            CB(50, 60, "Some other page"),   // no ticket — not this tab's business
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("42", row.TicketRef);
        Assert.Equal(TimeSpan.FromMinutes(20), row.Time);
        Assert.Equal(T0, row.FirstSeen);
        Assert.Equal(T0.AddMinutes(40), row.LastSeen);
    }

    [Fact]
    public void ATypedOverride_FilesItsActivityUnderTheTicketToo()
    {
        var rows = TicketsBuilder.Build(
            [CB(0, 15, "Acme - remote session", category: "ScreenConnect", overrideTicket: "77")]);

        Assert.Equal("77", Assert.Single(rows).TicketRef);
    }

    [Fact]
    public void Visits_CountDistinctSittings_NotTabSwitches()
    {
        // Two back-to-back blocks (one sitting) and one later return: 2 visits, matching the
        // Timesheet calendar's pin count.
        var rows = TicketsBuilder.Build(
        [
            CB(0, 5, "Ticket 42", ticket: "42"),
            CB(5.5, 8, "Ticket 42", ticket: "42"),
            CB(40, 45, "Ticket 42", ticket: "42"),
        ]);

        Assert.Equal(2, Assert.Single(rows).Visits);
    }

    [Fact]
    public void AppAndCategory_AreTheOnesTheMostTimeWentTo()
    {
        var rows = TicketsBuilder.Build(
        [
            CB(0, 5, "Ticket 42", ticket: "42", process: "msedge", category: "Halo"),
            CB(10, 40, "RE: 42 - Outlook", ticket: "42", process: "olk", category: "Outlook"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("olk", row.ProcessName);
        Assert.Equal("Outlook", row.Category);
    }

    [Fact]
    public void Rows_OrderByTimeSpent()
    {
        var rows = TicketsBuilder.Build(
        [
            CB(0, 5, "Ticket 1", ticket: "1"),
            CB(10, 40, "Ticket 2", ticket: "2"),
        ]);

        Assert.Equal(["2", "1"], rows.Select(r => r.TicketRef));
    }

    [Fact]
    public void ADayWithoutTickets_HasNoRows()
        => Assert.Empty(TicketsBuilder.Build([CB(0, 30, "Just browsing")]));
}
