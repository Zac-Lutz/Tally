using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class CallCategoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 9, 0, 0, TimeSpan.FromHours(-5));

    private static CallSpan Call(string process, string title = "Standup")
        => new(T0, T0.AddMinutes(30), process, title);

    [Theory]
    [InlineData("ms-teams", "Teams - Call")]
    [InlineData("msteams", "Teams - Call")]
    [InlineData("Teams", "Teams - Call")]
    [InlineData("Discord", "Discord")]
    [InlineData("discord", "Discord")]
    [InlineData("RingCentral", "RingCentral")]
    [InlineData("RingCentralPhone", "RingCentral")]
    [InlineData("Zoom", "Call")]
    [InlineData("slack", "Call")]
    public void ADayToDayApp_FilesItsCallsUnderItsOwnName(string process, string expected)
        => Assert.Equal(expected, Assert.Single(RollupBuilder.BuildCalls([Call(process)])).Category);

    [Fact]
    public void ARingCentralCall_IsTheWork_SoItOutranksTheWindowsUnderIt()
    {
        // It's the phone system: unlike sitting in a Discord voice channel, being on a
        // RingCentral call is the thing you were doing.
        var slots = SuggestionSlotBuilder.Build(
            [new ClassifiedBlock(
                new Block(T0, T0.AddMinutes(30), "chrome", "Tickets - Halo"),
                new Classification("Browsing", null, null, null, "rule"))],
            [Call("RingCentral", "Acme Corp")]);

        var slot = Assert.Single(slots);
        Assert.Equal(CallApps.RingCentralCategory, slot.Category);
        Assert.Equal(TimeSpan.FromMinutes(30), slot.Measured);
    }

    [Fact]
    public void CallsFromDifferentApps_NeverMergeIntoOneRow()
    {
        // Same title, two apps: they must stay two rows even though the label matches.
        var rows = RollupBuilder.BuildCalls([Call("ms-teams", "ms-teams"), Call("Discord", "Discord")]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Category == CallApps.TeamsCallCategory);
        Assert.Contains(rows, r => r.Category == CallApps.DiscordCategory);
    }

    [Fact]
    public void TheTimesheetFilesACallTheSameWayTheRollupDoes()
    {
        // One naming for a call, wherever it's shown — the export's bucket follows the rollup.
        var slots = SuggestionSlotBuilder.Build([], [Call("ms-teams", "MSP Ops Meeting")]);

        Assert.Equal(CallApps.TeamsCallCategory, Assert.Single(slots).Category);
    }
}

public class RollupBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock CB(
        double startMin, double endMin, string category, string title,
        string? ticket = null, string? subject = null, string process = "proc")
        => new(
            new Block(T0.AddMinutes(startMin), T0.AddMinutes(endMin), process, title),
            new Classification(category, null, ticket, subject, "rule"));

    [Fact]
    public void Rows_CarryTheAppTheTimeWasSpentIn_ClassifiedOrNot()
    {
        var rows = RollupBuilder.Build(
        [
            CB(0, 10, "Halo", "Tickets - Halo", process: "msedge"),
            CB(10, 20, Classification.Unclassified, "Mystery window", process: "someapp"),
        ]);

        Assert.Equal("msedge", rows.Single(r => r.Category == "Halo").ProcessName);
        Assert.Equal("someapp", rows.Single(r => r.Category == Classification.Unclassified).ProcessName);
    }

    [Fact]
    public void AMergedTicketRow_ShowsTheAppThatEarnedTheMostTime()
    {
        // One ticket viewed from two apps merges into one row; the app is the dominant one.
        var rows = RollupBuilder.Build(
        [
            CB(0, 5, "Halo", "Ticket #42 - quick look", ticket: "42", process: "chrome"),
            CB(10, 40, "Halo", "Ticket #42 - real work", ticket: "42", process: "msedge"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("msedge", row.ProcessName);
    }

    [Fact]
    public void CallRows_CarryTheApp_AndTimerRows_HaveNone()
    {
        var call = Assert.Single(RollupBuilder.BuildCalls(
            [new CallSpan(T0, T0.AddMinutes(30), "ms-teams", "Standup")]));
        Assert.Equal("ms-teams", call.ProcessName);

        var timer = Assert.Single(RollupBuilder.BuildTimers(
            [new ManualTimer { Id = 1, Name = "Phone call", Start = T0, End = T0.AddMinutes(10) }]));
        Assert.Null(timer.ProcessName);
    }

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
    public void BuildCalls_SumsSameAppAcrossTheDay()
    {
        var rows = RollupBuilder.BuildCalls(
        [
            Call(0, 20, "Discord"),
            Call(120, 145, "Discord"),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("Discord", row.Category);   // a day-to-day app files under its own name
        Assert.Equal("Discord", row.DetailName);
        Assert.Equal(TimeSpan.FromMinutes(45), row.Time);   // 20 + 25
    }

    [Fact]
    public void BuildCalls_UsesThePlainCallCategory_ForAnyOtherApp()
    {
        var row = Assert.Single(RollupBuilder.BuildCalls([Call(0, 20, "Zoom")]));

        Assert.Equal(RollupBuilder.CallCategory, row.Category);
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
