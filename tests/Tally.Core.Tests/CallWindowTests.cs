using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// Recognising a call from its window rather than from the microphone. The mic answers whether you
/// are talking; a meeting you sit and listen to is still a meeting, and used to record as nothing.
/// Every title here is one Tally actually captured from the user's own Teams.
/// </summary>
public class MeetingWindowTests
{
    [Theory]
    // The meeting window, in each of the three shapes Teams gives it.
    [InlineData("Brandon/Zac Touchpoint | Microsoft Teams", "Brandon/Zac Touchpoint")]
    [InlineData("Meeting join | THEE Service Meeting | Microsoft Teams", "THEE Service Meeting")]
    [InlineData("Meeting compact view | Brandon/Zac Touchpoint | Microsoft Teams", "Brandon/Zac Touchpoint")]
    [InlineData("MSP Ops Meeting | Microsoft Teams", "MSP Ops Meeting")]
    [InlineData("Security Advisory Committee | Microsoft Teams", "Security Advisory Committee")]
    // A one-to-one call names the person.
    [InlineData("Logan Brown | Microsoft Teams", "Logan Brown")]
    public void AMeetingWindow_NamesItsMeeting(string title, string expected)
        => Assert.Equal(expected, CallApps.MeetingName("ms-teams", title));

    [Theory]
    // The main window, whatever section it is showing. None of these is a call.
    [InlineData("Microsoft Teams")]
    [InlineData("Chat | Ammi Jacobus | Microsoft Teams")]
    [InlineData("Chat | Service Family | Microsoft Teams")]
    [InlineData("Chat | THEE Service Meeting | Microsoft Teams")]
    [InlineData("Calendar | Microsoft Teams")]
    [InlineData("Activity | Microsoft Teams")]
    [InlineData("Calls | Microsoft Teams")]
    [InlineData("")]
    // Not Teams at all.
    [InlineData("Inbox - Outlook")]
    public void EverythingElse_IsNotAMeeting(string title)
        => Assert.Null(CallApps.MeetingName("ms-teams", title));

    [Fact]
    public void OnlyTeamsIsReadThisWay_ForNow()
    {
        // Discord sits in a voice channel for hours and that time isn't Discord's to claim, and
        // RingCentral hasn't been observed making a real call yet — a guessed pattern would be
        // worse than none.
        Assert.Null(CallApps.MeetingName("Discord", "meet here | Lutz Tech - Discord"));
        Assert.Null(CallApps.MeetingName("ringcentral", "Active call | RingCentral"));
    }

    [Fact]
    public void TheProcessNameIsMatchedTheSameWayTheCategoryIs()
    {
        foreach (var process in new[] { "ms-teams", "msteams", "Teams", "MS-TEAMS" })
            Assert.Equal("Standup", CallApps.MeetingName(process, "Standup | Microsoft Teams"));
    }
}

/// <summary>The call window as a second witness alongside the microphone, in the sessionizer.</summary>
public class CallWindowSpanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(-5));

    private static TrackedEvent Ev(EventKind kind, double atSeconds, string process, string title = "")
        => new() { Timestamp = T0.AddSeconds(atSeconds), Kind = kind, ProcessName = process, WindowTitle = title };

    [Fact]
    public void AMeetingWithTheMicNeverOn_IsStillACall()
    {
        // The whole point: an hour of listening, muted throughout, while working in other windows.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "THEE Service Meeting"),
            Ev(EventKind.Focus, 10, "msedge", "Tickets > Management - Work - Microsoft Edge"),
            Ev(EventKind.Focus, 1800, "msedge", "Mail - Outlook - Work - Microsoft Edge"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "THEE Service Meeting"),
        ], T0.AddSeconds(3700));

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromHours(1), call.Duration);
        Assert.Equal("THEE Service Meeting", call.Title);
    }

    [Fact]
    public void MutingHalfwayThrough_DoesNotEndTheCall()
    {
        // The reported bug, in miniature: the mic goes quiet fifteen minutes in and the meeting
        // runs another forty-five. Before the window was watched, this recorded fifteen minutes.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "MSP Ops Meeting"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.MicEnd, 900, "ms-teams"),
            Ev(EventKind.Focus, 905, "msedge", "Tickets - Work - Microsoft Edge"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "MSP Ops Meeting"),
        ], T0.AddSeconds(3700));

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromHours(1), call.Duration);
    }

    [Fact]
    public void TheWindowNamesTheCall_EvenWhenTheMicSawItFirst()
    {
        // The old naming bug: the mic span was titled from whatever Teams window happened to be
        // focused, so a meeting was recorded as the chat someone was reading during it.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.Focus, 5, "ms-teams", "Chat | Service Family | Microsoft Teams"),
            Ev(EventKind.CallWindowOpen, 30, "ms-teams", "THEE Service Meeting"),
            Ev(EventKind.MicEnd, 1800, "ms-teams"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "THEE Service Meeting"),
        ], T0.AddSeconds(3700));

        var call = Assert.Single(result.Calls);
        Assert.Equal("THEE Service Meeting", call.Title);
        // And the two witnesses are one call covering both, not two overlapping ones.
        Assert.Equal(T0, call.Start);
        Assert.Equal(T0.AddSeconds(3600), call.End);
    }

    [Fact]
    public void AMicSpanOutlastingTheWindow_ExtendsTheSameCall()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "Standup"),
            Ev(EventKind.MicStart, 60, "ms-teams"),
            Ev(EventKind.CallWindowClose, 1800, "ms-teams", "Standup"),
            Ev(EventKind.MicEnd, 1900, "ms-teams"),
        ], T0.AddSeconds(2000));

        var call = Assert.Single(result.Calls);
        Assert.Equal(T0, call.Start);
        Assert.Equal(T0.AddSeconds(1900), call.End);
    }

    [Fact]
    public void BackToBackMeetings_StayTwoCalls()
    {
        // Leaving one meeting and joining the next takes seconds. Two names, two calls — the
        // thing the title-matching merge exists to protect.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "MSP Ops Meeting"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "MSP Ops Meeting"),
            Ev(EventKind.CallWindowOpen, 3610, "ms-teams", "Security Advisory Committee"),
            Ev(EventKind.CallWindowClose, 5400, "ms-teams", "Security Advisory Committee"),
        ], T0.AddSeconds(5500));

        Assert.Equal(2, result.Calls.Count);
        Assert.Equal("MSP Ops Meeting", result.Calls[0].Title);
        Assert.Equal("Security Advisory Committee", result.Calls[1].Title);
    }

    [Fact]
    public void ARestartMidMeeting_DoesNotLeaveTheCallRunningAllDay()
    {
        // Tally can't vouch for a meeting it wasn't watching. The span closes at the restart and
        // the next poll reopens it; same name across a short gap, so it reads as one meeting.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "MSP Ops Meeting"),
            Ev(EventKind.Startup, 1800, ""),
            Ev(EventKind.CallWindowOpen, 1803, "ms-teams", "MSP Ops Meeting"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "MSP Ops Meeting"),
        ], T0.AddSeconds(3700));

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromHours(1), call.Duration);
    }

    [Fact]
    public void ACallStillOpenAtTheEndOfTheDay_RunsToTheEndOfWhatWasRecorded()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "Standup"),
        ], T0.AddSeconds(1800));

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Duration);
        Assert.Equal("Standup", call.Title);
    }

    [Fact]
    public void ACallOutranksTheWindowsUnderIt_OnTheTimesheet()
    {
        // Calls already beat window activity once Tally knows about them; this pins that the new
        // witness reaches that machinery, since knowing about the call was the only thing missing.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.CallWindowOpen, 0, "ms-teams", "THEE Service Meeting"),
            Ev(EventKind.Focus, 10, "msedge", "Tickets - Work - Microsoft Edge"),
            Ev(EventKind.CallWindowClose, 3600, "ms-teams", "THEE Service Meeting"),
        ], T0.AddSeconds(3600));

        var classified = result.Blocks
            .Select(b => new ClassifiedBlock(b, new Classification("Development", null, null, null, "r")))
            .ToList();

        var slots = SuggestionSlotBuilder.Build(classified, result.Calls, []);
        var call = Assert.Single(slots, s => s.Kind == SuggestionSlotKind.Call);

        Assert.Equal("THEE Service Meeting", call.Label);
        Assert.Equal(TimeSpan.FromHours(1), call.Measured);
        // The hour is the meeting's; the Edge window underneath doesn't get billed for it too.
        Assert.All(slots.Where(s => s.Kind != SuggestionSlotKind.Call),
            s => Assert.True(s.Measured < TimeSpan.FromMinutes(1)));
    }
}
