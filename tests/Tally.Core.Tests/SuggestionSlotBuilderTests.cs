using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class SuggestionSlotBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 8, 0, 0, TimeSpan.FromHours(-5));

    private static ClassifiedBlock CB(double startMin, double endMin, string category, string title, string? ticket = null)
        => new(
            new Block(T0.AddMinutes(startMin), T0.AddMinutes(endMin), "chrome", title),
            new Classification(category, null, ticket, null, "rule"));

    private static CallSpan Call(double startMin, double endMin, string title = "Standup | Microsoft Teams")
        => new(T0.AddMinutes(startMin), T0.AddMinutes(endMin), "ms-teams", title);

    private static ManualTimer Timer(double startMin, double endMin, string name)
        => new() { Name = name, Start = T0.AddMinutes(startMin), End = T0.AddMinutes(endMin) };

    // ---- Rounding ----

    [Theory]
    [InlineData(2, 5)]      // never rounds to nothing — 2 minutes of work is not zero minutes
    [InlineData(8, 10)]
    [InlineData(7, 5)]
    [InlineData(13, 15)]
    [InlineData(30, 30)]
    public void Round_GoesToTheNearestFiveMinutes_ButNeverToZero(int measured, int expected)
        => Assert.Equal(
            TimeSpan.FromMinutes(expected),
            SuggestionSlotBuilder.Round(TimeSpan.FromMinutes(measured), TimeSpan.FromMinutes(5)));

    // ---- Grouping ----

    [Fact]
    public void OneTicketWorkedInSeveralApps_IsOneSlot()
    {
        // The billing target is the ticket, not the app it was worked in.
        var slots = SuggestionSlotBuilder.Build(
        [
            CB(0, 20, "HaloPSA", "Ticket #4867 - mailbox rules", ticket: "4867"),
            CB(20, 40, "Remote Support", "Acme - ScreenConnect", ticket: "4867"),
        ]);

        var slot = Assert.Single(slots);
        Assert.Equal("4867", slot.TicketRef);
        Assert.Equal(TimeSpan.FromMinutes(40), slot.Measured);
    }

    [Fact]
    public void TheSameTargetMorningAndAfternoon_IsTwoSlots()
    {
        var slots = SuggestionSlotBuilder.Build(
        [
            CB(0, 30, "Development", "Tally"),
            CB(180, 210, "Development", "Tally"),   // three hours later
        ]);

        Assert.Equal(2, slots.Count);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(30), s.Measured));
    }

    [Fact]
    public void ShortSwitchesAwayAndBack_StayOneSession()
    {
        var slots = SuggestionSlotBuilder.Build(
        [
            CB(0, 20, "Development", "Tally"),
            CB(20, 25, "Browsing", "Docs"),         // a five-minute detour
            CB(25, 45, "Development", "Tally"),
        ]);

        var dev = Assert.Single(slots, s => s.Category == "Development");
        Assert.Equal(TimeSpan.FromMinutes(40), dev.Measured);
    }

    // ---- Nothing is lost ----

    [Fact]
    public void ManyShortVisitsToOneTicket_ArePooledIntoOneSlot_NotDropped()
    {
        // Six two-minute visits, each far apart: no single session clears the minimum, but twelve
        // minutes of real work must not vanish.
        var blocks = Enumerable.Range(0, 6)
            .Select(i => CB(i * 60, i * 60 + 2, "HaloPSA", "Ticket #4867", ticket: "4867"))
            .ToList();

        var slot = Assert.Single(SuggestionSlotBuilder.Build(blocks));

        Assert.Equal("4867", slot.TicketRef);
        Assert.Equal(TimeSpan.FromMinutes(12), slot.Measured);
        Assert.Equal(TimeSpan.FromMinutes(10), slot.Reported);
    }

    [Fact]
    public void ScatteredNoise_LandsInOneOddsAndEndsSlot_RatherThanDisappearing()
    {
        var blocks = new[]
        {
            CB(0, 2, "Admin", "Explorer"),
            CB(60, 62, "Browsing", "Some page"),
            CB(120, 122, "Email", "Inbox"),
        };

        var slot = Assert.Single(SuggestionSlotBuilder.Build(blocks));

        Assert.Equal(SuggestionSlotKind.OddsAndEnds, slot.Kind);
        Assert.Equal(TimeSpan.FromMinutes(6), slot.Measured);
    }

    [Fact]
    public void PooledWork_IsDrawnAtItsStartForTheTimeItEarned_NotSmearedAcrossTheDay()
    {
        var blocks = Enumerable.Range(0, 6)
            .Select(i => CB(i * 60, i * 60 + 2, "HaloPSA", "Ticket #4867", ticket: "4867"))
            .ToList();

        var slot = Assert.Single(SuggestionSlotBuilder.Build(blocks));

        Assert.Equal(T0, slot.Start);
        Assert.Equal(slot.Start + slot.Reported, slot.End);   // not T0 + 5 hours
    }

    // ---- Priority: no minute billed twice ----

    [Fact]
    public void AMeetingOutranksTheWindowsOpenDuringIt()
    {
        // An hour of meeting while reading a ticket is an hour of meeting, counted once.
        var slots = SuggestionSlotBuilder.Build(
            [CB(0, 60, "Browsing", "Tickets - Halo")],
            [Call(0, 60, "Security Advisory Committee | Microsoft Teams")]);

        var slot = Assert.Single(slots);
        Assert.Equal(SuggestionSlotKind.Call, slot.Kind);
        Assert.Equal(TimeSpan.FromHours(1), slot.Measured);
        // The window activity survives as detail — it's what makes the note writable.
        Assert.Contains(slot.Blocks, b => b.Block.Title == "Tickets - Halo");
    }

    [Fact]
    public void WindowTimeEitherSideOfAMeeting_StillCounts()
    {
        var slots = SuggestionSlotBuilder.Build(
            [CB(0, 120, "Development", "Tally")],
            [Call(30, 90, "Standup | Microsoft Teams")]);

        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(slots, s => s.Kind == SuggestionSlotKind.Call).Measured);

        // The meeting cuts the two hours in half, and the halves are an hour apart — so they're
        // two sessions sitting either side of it, not one entry pretending to span the meeting.
        var activity = slots.Where(s => s.Kind == SuggestionSlotKind.Activity).ToList();
        Assert.Equal(2, activity.Count);
        Assert.All(activity, s => Assert.Equal(TimeSpan.FromMinutes(30), s.Measured));
    }

    [Fact]
    public void ATimerOutranksAMeetingUnderneathIt()
    {
        var slots = SuggestionSlotBuilder.Build(
            [CB(0, 60, "Browsing", "Tickets - Halo")],
            [Call(0, 60)],
            [Timer(0, 30, "Ticket #123 phone call")]);

        var timer = Assert.Single(slots, s => s.Kind == SuggestionSlotKind.Timer);
        Assert.Equal(TimeSpan.FromMinutes(30), timer.Measured);

        // The call keeps only the half hour the timer didn't claim.
        var call = Assert.Single(slots, s => s.Kind == SuggestionSlotKind.Call);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Measured);
    }

    [Fact]
    public void TheDayNeverBillsMoreTimeThanItObserved()
    {
        var slots = SuggestionSlotBuilder.Build(
            [CB(0, 120, "Development", "Tally")],
            [Call(30, 90)],
            [Timer(40, 50, "Ticket #123 call")]);

        // Measured across every lane equals the two hours of wall clock, split not duplicated.
        var total = TimeSpan.FromTicks(slots.Sum(s => s.Measured.Ticks));
        Assert.Equal(TimeSpan.FromHours(2), total);
    }

    [Fact]
    public void AMomentaryMicBlip_DoesNotClaimTimeFromTheWindowLane()
    {
        // Two minutes isn't a meeting; letting it claim would strand the time in a slot too small
        // to keep, so the window lane keeps counting it.
        var slots = SuggestionSlotBuilder.Build(
            [CB(0, 60, "Development", "Tally")],
            [Call(10, 12)]);

        var slot = Assert.Single(slots);
        Assert.Equal(SuggestionSlotKind.Activity, slot.Kind);
        Assert.Equal(TimeSpan.FromHours(1), slot.Measured);
    }

    [Fact]
    public void ATimerIsAlwaysItsOwnLine_HoweverShort()
    {
        // Deliberate beats tidy: the user started it, so it gets a line.
        var slots = SuggestionSlotBuilder.Build([], [], [Timer(0, 2, "Quick call")]);

        var slot = Assert.Single(slots);
        Assert.Equal(SuggestionSlotKind.Timer, slot.Kind);
        Assert.Equal("Quick call", slot.Label);
        Assert.Equal(TimeSpan.FromMinutes(5), slot.Reported);
    }

    [Fact]
    public void AnEmptyDay_ProducesNoSlots()
        => Assert.Empty(SuggestionSlotBuilder.Build([]));

    // ---- Export window ----

    private static SuggestionSlotOptions Window(string? from, string? to) => new()
    {
        WindowStart = from is null ? null : TimeOnly.Parse(from),
        WindowEnd = to is null ? null : TimeOnly.Parse(to),
    };

    [Fact]
    public void NoWindow_KeepsTheWholeDay()
    {
        var day = new[] { CB(0, 30, "Development", "Morning"), CB(360, 400, "Development", "Afternoon") };

        Assert.Equal(2, SuggestionSlotBuilder.Build(day).Count);
    }

    [Fact]
    public void AWindow_KeepsOnlyWhatStartedInsideIt()
    {
        // T0 is 08:00; the second block starts at 14:00.
        var day = new[] { CB(0, 30, "Development", "Morning"), CB(360, 400, "Development", "Afternoon") };

        var morning = SuggestionSlotBuilder.Build(day, options: Window(null, "12:00"));
        var afternoon = SuggestionSlotBuilder.Build(day, options: Window("12:00", null));

        Assert.Equal("Morning", Assert.Single(morning).Label);
        Assert.Equal("Afternoon", Assert.Single(afternoon).Label);
    }

    [Fact]
    public void ASlotStraddlingTheCutOff_BelongsOnlyToTheHalfItStartedIn()
    {
        // A meeting from 11:30 to 12:30 with a noon split must be billed once, not in both halves.
        var calls = new[] { Call(210, 270, "Long meeting | Microsoft Teams") };

        var morning = SuggestionSlotBuilder.Build([], calls, options: Window(null, "12:00"));
        var afternoon = SuggestionSlotBuilder.Build([], calls, options: Window("12:00", null));

        Assert.Single(morning);
        Assert.Empty(afternoon);
    }

    [Fact]
    public void TwoSlicesOfADay_CoverExactlyTheWholeDayBetweenThem()
    {
        var day = new[] { CB(0, 90, "Development", "Morning"), CB(300, 400, "Browsing", "Afternoon") };
        var whole = SuggestionSlotBuilder.Build(day);

        var split = SuggestionSlotBuilder.Build(day, options: Window(null, "12:00"))
            .Concat(SuggestionSlotBuilder.Build(day, options: Window("12:00", null)))
            .ToList();

        Assert.Equal(whole.Count, split.Count);
        Assert.Equal(
            TimeSpan.FromTicks(whole.Sum(s => s.Reported.Ticks)),
            TimeSpan.FromTicks(split.Sum(s => s.Reported.Ticks)));
    }
}
