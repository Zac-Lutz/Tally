using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class TimesheetCalendarTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 8, 0, 0, TimeSpan.FromHours(-5));

    private static SuggestionSlot Slot(double startMin, double endMin, string label = "Work")
        => new(
            SuggestionSlotKind.Activity, "Development", null, label,
            T0.AddMinutes(startMin), T0.AddMinutes(endMin),
            TimeSpan.FromMinutes(endMin - startMin), TimeSpan.FromMinutes(endMin - startMin), []);

    private static ClassifiedBlock Visit(double startMin, double endMin)
        => new(
            new Block(T0.AddMinutes(startMin), T0.AddMinutes(endMin), "proc", "Ticket window"),
            new Classification("Halo", null, "42", null, "rule"));

    /// <summary>A pooled slot: officially compact (End = Start + reported) but visited later too.</summary>
    private static SuggestionSlot Pooled(params (double Start, double End)[] visits)
        => new(
            SuggestionSlotKind.Activity, "Halo", "42", "Ticket #42",
            T0.AddMinutes(visits[0].Start), T0.AddMinutes(visits[0].Start + 15),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15),
            visits.Select(v => Visit(v.Start, v.End)).ToList());

    [Fact]
    public void DisplaySpan_StretchesAnActivitySlot_ToItsLastVisit()
    {
        var slot = Pooled((0, 5), (25, 30), (55, 60));

        var (start, end) = TimesheetCalendar.DisplaySpan(slot);

        Assert.Equal(T0, start);
        Assert.Equal(T0.AddMinutes(60), end);   // the envelope, not the compact End
    }

    [Fact]
    public void DisplaySpan_OfACallOrOddsAndEnds_IsItsOwnSpan()
    {
        var call = new SuggestionSlot(
            SuggestionSlotKind.Call, "Teams - Call", null, "Standup",
            T0, T0.AddMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30),
            [Visit(0, 90)]);   // underlying detail must NOT stretch a call

        Assert.Equal((T0, T0.AddMinutes(30)), TimesheetCalendar.DisplaySpan(call));
    }

    [Fact]
    public void Visits_MergeBlocksWithinAMinute_AndKeepRealGapsApart()
    {
        // Two back-to-back blocks (a tab switch) and one later return: two visits, not three.
        var slot = Pooled((0, 5), (5.5, 8), (40, 45));

        var visits = TimesheetCalendar.Visits(slot);

        Assert.Equal(2, visits.Count);
        Assert.Equal((T0, T0.AddMinutes(8)), visits[0]);
        Assert.Equal((T0.AddMinutes(40), T0.AddMinutes(45)), visits[1]);
    }

    [Fact]
    public void Bounds_AndLayout_FollowTheEnvelope()
    {
        // The pooled slot officially ends at :15 but was revisited at :55 — the grid must reach
        // 9:00, and a slot at :30 must share width with the envelope crossing it.
        var pooled = Pooled((0, 5), (55, 60));
        var other = Slot(30, 50, "Other");

        var bounds = TimesheetCalendar.Bounds([pooled, other]);
        Assert.Equal(T0.AddMinutes(60), bounds!.Value.End);

        var entries = TimesheetCalendar.Lay([pooled, other]);
        Assert.All(entries, e => Assert.Equal(2, e.Columns));
    }

    [Fact]
    public void SlotsThatDoNotOverlap_EachTakeTheFullWidth()
    {
        var entries = TimesheetCalendar.Lay([Slot(0, 30), Slot(30, 60), Slot(90, 120)]);

        Assert.All(entries, e => Assert.Equal(1, e.Columns));
        Assert.All(entries, e => Assert.Equal(0, e.Column));
    }

    [Fact]
    public void OverlappingSlots_SitSideBySideAtEqualWidths()
    {
        var entries = TimesheetCalendar.Lay([Slot(0, 60, "A"), Slot(30, 90, "B")]);

        Assert.All(entries, e => Assert.Equal(2, e.Columns));
        Assert.Equal([0, 1], entries.Select(e => e.Column));
    }

    [Fact]
    public void AColumnIsReusedOnceItsSlotHasEnded()
    {
        // A and B overlap; C starts after A ends, so it takes A's column rather than a third.
        var entries = TimesheetCalendar.Lay([Slot(0, 30, "A"), Slot(10, 90, "B"), Slot(40, 80, "C")]);

        Assert.All(entries, e => Assert.Equal(2, e.Columns));
        Assert.Equal(0, Assert.Single(entries, e => e.Slot.Label == "C").Column);
    }

    [Fact]
    public void AFreshRunStartsBackAtFullWidth()
    {
        // Two overlapping, then a gap: the later slot must not inherit the crowded run's width.
        var entries = TimesheetCalendar.Lay([Slot(0, 60, "A"), Slot(30, 90, "B"), Slot(120, 150, "C")]);

        Assert.Equal(1, Assert.Single(entries, e => e.Slot.Label == "C").Columns);
    }

    [Fact]
    public void EverySlotIsPlaced_EvenWhenTheyAllOverlap()
    {
        var slots = Enumerable.Range(0, 5).Select(i => Slot(i, 60, $"S{i}")).ToList();

        var entries = TimesheetCalendar.Lay(slots);

        Assert.Equal(5, entries.Count);
        Assert.Equal([0, 1, 2, 3, 4], entries.Select(e => e.Column).Order());
    }

    [Fact]
    public void Bounds_RoundOutToTheRulingLines()
    {
        // 8:10–8:50 draws a grid from 8:00 to 9:00 — no more than half an hour of empty grid.
        var bounds = TimesheetCalendar.Bounds([Slot(10, 50)]);

        Assert.NotNull(bounds);
        Assert.Equal(T0, bounds.Value.Start);
        Assert.Equal(T0.AddHours(1), bounds.Value.End);
    }

    [Fact]
    public void Bounds_OfWorkInsideOneInterval_StillGiveAGridToSitOn()
    {
        var bounds = TimesheetCalendar.Bounds([Slot(5, 10)]);

        Assert.NotNull(bounds);
        Assert.True(bounds.Value.End > bounds.Value.Start);
    }

    [Fact]
    public void Bounds_OfAnEmptyDay_AreNothing()
        => Assert.Null(TimesheetCalendar.Bounds([]));
}
