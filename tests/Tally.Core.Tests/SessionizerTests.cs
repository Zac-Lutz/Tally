using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class SessionizerUrlTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 9, 0, 0, TimeSpan.FromHours(-5));

    private static TrackedEvent Ev(EventKind kind, double atSeconds, string process, string title, string? url = null)
        => new() { Timestamp = T0.AddSeconds(atSeconds), Kind = kind, ProcessName = process, WindowTitle = title, Url = url };

    [Fact]
    public void ABlock_CarriesTheUrl_OfTheEventThatOpenedIt()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "msedge", "Tickets - Halo", "lutz.halopsa.com/tickets"),
            Ev(EventKind.TitleChange, 300, "msedge", "Mail - Outlook", "outlook.office.com/mail"),
            Ev(EventKind.Focus, 600, "devenv", "Fix.cs"),
        ], T0.AddSeconds(900));

        Assert.Equal("lutz.halopsa.com/tickets", result.Blocks[0].Url);
        Assert.Equal("outlook.office.com/mail", result.Blocks[1].Url);
        Assert.Null(result.Blocks[2].Url);   // not a browser — no URL, as before
    }
}

public class SessionizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));

    private static TrackedEvent Ev(EventKind kind, double seconds, string process = "", string title = "")
        => new()
        {
            Timestamp = T0.AddSeconds(seconds),
            Kind = kind,
            ProcessName = process,
            WindowTitle = title,
        };

    [Fact]
    public void FocusSwitches_CreateBlocks_WithVerbatimTitles()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "Ticket #12345 - VPN drops - HaloPSA"),
            Ev(EventKind.Focus, 600, "ms-teams", "Chat | Zac | Microsoft Teams"),
        ], T0.AddSeconds(1200));

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Ticket #12345 - VPN drops - HaloPSA", result.Blocks[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(600), result.Blocks[0].Duration);
        Assert.Equal("ms-teams", result.Blocks[1].ProcessName);
        Assert.Equal(T0.AddSeconds(1200), result.Blocks[1].End);
    }

    [Fact]
    public void TitleChange_SplitsBlock_LikeABrowserTabSwitch()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "Inbox - Outlook"),
            Ev(EventKind.TitleChange, 300, "chrome", "Ticket #99 - HaloPSA"),
        ], T0.AddSeconds(600));

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Inbox - Outlook", result.Blocks[0].Title);
        Assert.Equal("Ticket #99 - HaloPSA", result.Blocks[1].Title);
    }

    [Fact]
    public void FlickerBlocks_AreDropped_AndSameKeyNeighborsMerge()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "A"),
            Ev(EventKind.Focus, 60, "explorer", "B"),   // 3-second flicker
            Ev(EventKind.Focus, 63, "chrome", "A"),
        ], T0.AddSeconds(120));

        var block = Assert.Single(result.Blocks);
        Assert.Equal("chrome", block.ProcessName);
        Assert.Equal(T0, block.Start);
        Assert.Equal(T0.AddSeconds(120), block.End);
    }

    [Fact]
    public void Idle_ClosesBlockAtBackdatedTime_AndResumesOnIdleEnd()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "A"),
            Ev(EventKind.IdleStart, 600),   // capture backdates this to the last input
            Ev(EventKind.IdleEnd, 900),
        ], T0.AddSeconds(1200));

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(T0.AddSeconds(600), result.Blocks[0].End);
        Assert.Equal(T0.AddSeconds(900), result.Blocks[1].Start);

        var idle = Assert.Single(result.InactivePeriods);
        Assert.Equal(InactiveReasons.Idle, idle.Reason);
        Assert.Equal(TimeSpan.FromSeconds(300), idle.Duration);
    }

    [Fact]
    public void IdleDuringActiveCall_IsSuppressed()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "A"),
            Ev(EventKind.MicStart, 120, "ms-teams"),
            Ev(EventKind.IdleStart, 300),   // sitting back listening on the call
            Ev(EventKind.IdleEnd, 540),
            Ev(EventKind.MicEnd, 720, "ms-teams"),
        ], T0.AddSeconds(900));

        var block = Assert.Single(result.Blocks);   // never interrupted
        Assert.Equal(T0, block.Start);
        Assert.Equal(T0.AddSeconds(900), block.End);
        Assert.Empty(result.InactivePeriods);

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromSeconds(600), call.Duration);
    }

    [Fact]
    public void Lock_SuspendsForeground_ButCallSpanContinues()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "Standup | Microsoft Teams"),
            Ev(EventKind.MicStart, 60, "ms-teams"),
            Ev(EventKind.Lock, 300),
            Ev(EventKind.Unlock, 480),
            Ev(EventKind.MicEnd, 720, "ms-teams"),
        ], T0.AddSeconds(900));

        Assert.Equal(2, result.Blocks.Count);

        var locked = Assert.Single(result.InactivePeriods);
        Assert.Equal(InactiveReasons.Locked, locked.Reason);

        var call = Assert.Single(result.Calls);
        Assert.Equal(T0.AddSeconds(60), call.Start);
        Assert.Equal(T0.AddSeconds(720), call.End);   // uninterrupted by the lock
    }

    [Fact]
    public void MicSpans_WithBriefGaps_MergeIntoOneCall()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "Standup | Microsoft Teams"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.MicEnd, 300, "ms-teams"),
            Ev(EventKind.MicStart, 315, "ms-teams"),   // 15-second blip, same meeting
            Ev(EventKind.MicEnd, 600, "ms-teams"),
        ], T0.AddSeconds(900));

        var call = Assert.Single(result.Calls);
        Assert.Equal(T0, call.Start);
        Assert.Equal(T0.AddSeconds(600), call.End);
    }

    [Fact]
    public void BackToBackMeetings_StayTwoCalls_EvenThoughTheMicGapIsSeconds()
    {
        // Real shape of leaving one Teams meeting and joining the next: the mic drops for ten
        // seconds — well inside the merge gap — and only the window title says it's a new call.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "MSP Ops Meeting | Microsoft Teams"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.MicEnd, 3600, "ms-teams"),
            Ev(EventKind.Focus, 3607, "ms-teams", "Microsoft Teams"),
            Ev(EventKind.TitleChange, 3609, "ms-teams", "Security Advisory Committee | Microsoft Teams"),
            Ev(EventKind.MicStart, 3610, "ms-teams"),
            Ev(EventKind.MicEnd, 5400, "ms-teams"),
        ], T0.AddSeconds(6000));

        Assert.Equal(2, result.Calls.Count);
        Assert.Equal(TimeSpan.FromHours(1), result.Calls[0].Duration);
        Assert.Equal("MSP Ops Meeting | Microsoft Teams", result.Calls[0].Title);
        Assert.Equal(T0.AddSeconds(3610), result.Calls[1].Start);
        Assert.Equal("Security Advisory Committee | Microsoft Teams", result.Calls[1].Title);
    }

    [Fact]
    public void ADroppedMicRejoiningTheSameMeeting_IsStillOneCall()
    {
        // The other side of the same coin: same title across the gap, so it's one meeting.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "MSP Ops Meeting | Microsoft Teams"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.MicEnd, 1800, "ms-teams"),
            Ev(EventKind.Focus, 1805, "ms-teams", "MSP Ops Meeting | Microsoft Teams"),
            Ev(EventKind.MicStart, 1810, "ms-teams"),
            Ev(EventKind.MicEnd, 3600, "ms-teams"),
        ], T0.AddSeconds(4000));

        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(result.Calls).Duration);
    }

    [Fact]
    public void ARestartBetweenMeetings_DoesNotLeaveTheFirstCallRunning()
    {
        // Tally was down when the first meeting ended, so its MicEnd was never recorded. Without
        // the startup marker the span would run to the end of the day and swallow the second call.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "MSP Ops Meeting | Microsoft Teams"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.Startup, 3600),                 // restarted; first meeting already over
            Ev(EventKind.Focus, 3900, "ms-teams", "Security Advisory Committee | Microsoft Teams"),
            Ev(EventKind.MicStart, 3910, "ms-teams"),
            Ev(EventKind.MicEnd, 5400, "ms-teams"),
        ], T0.AddSeconds(6000));

        Assert.Equal(2, result.Calls.Count);
        Assert.Equal(T0.AddSeconds(3600), result.Calls[0].End);   // closed at the restart
        Assert.Equal("Security Advisory Committee | Microsoft Teams", result.Calls[1].Title);
    }

    [Fact]
    public void ARestartDuringAMeeting_LeavesItAsOneCall()
    {
        // The common case — Tally restarts mid-meeting and the mic is still live seconds later.
        // The span is closed and reopened, then merged back by the matching title.
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "MSP Ops Meeting | Microsoft Teams"),
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.Startup, 1800),
            Ev(EventKind.MicStart, 1803, "ms-teams"),
            Ev(EventKind.MicEnd, 3600, "ms-teams"),
        ], T0.AddSeconds(4000));

        var call = Assert.Single(result.Calls);
        Assert.Equal(TimeSpan.FromHours(1), call.Duration);
    }

    [Fact]
    public void CallSpan_TakesTitleFromProcessWindowDuringSpan()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.MicStart, 0, "ms-teams"),
            Ev(EventKind.Focus, 30, "ms-teams", "Weekly standup | Microsoft Teams"),
            Ev(EventKind.MicEnd, 600, "ms-teams"),
        ], T0.AddSeconds(900));

        Assert.Equal("Weekly standup | Microsoft Teams", Assert.Single(result.Calls).Title);
    }

    [Fact]
    public void CallSpan_FallsBackToTitleSeenBeforeTheSpan()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "ms-teams", "Weekly standup | Microsoft Teams"),
            Ev(EventKind.Focus, 30, "chrome", "Inbox - Outlook"),
            Ev(EventKind.MicStart, 60, "ms-teams"),
            Ev(EventKind.MicEnd, 600, "ms-teams"),
        ], T0.AddSeconds(900));

        Assert.Equal("Weekly standup | Microsoft Teams", Assert.Single(result.Calls).Title);
    }

    [Fact]
    public void OpenBlock_AndOpenCall_CloseAtEndOfData()
    {
        var result = Sessionizer.Build(
        [
            Ev(EventKind.Focus, 0, "chrome", "A"),
            Ev(EventKind.MicStart, 60, "ms-teams"),
        ], T0.AddSeconds(300));

        Assert.Equal(T0.AddSeconds(300), Assert.Single(result.Blocks).End);
        Assert.Equal(T0.AddSeconds(300), Assert.Single(result.Calls).End);
    }
}
