using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

public class MonthGridTests
{
    [Fact]
    public void Build_StartsOnTheSundayOnOrBeforeTheFirst()
    {
        // 1 Aug 2026 is a Saturday, so the grid opens on Sunday 26 July.
        var cells = MonthGrid.Build(2026, 8);

        Assert.Equal(new DateOnly(2026, 7, 26), cells[0]);
        Assert.Equal(DayOfWeek.Sunday, cells[0].DayOfWeek);
    }

    [Fact]
    public void Build_KeepsAWholeWeekAheadOfAMonthThatOpensOnTheFirstDayOfTheWeek()
    {
        // 1 Feb 2026 IS a Sunday: the grid starts on it rather than on a blank week above it.
        var cells = MonthGrid.Build(2026, 2);

        Assert.Equal(new DateOnly(2026, 2, 1), cells[0]);
    }

    [Fact]
    public void Build_IsAlwaysSixWeeksAndHoldsEveryDayOfTheMonth()
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            var cells = MonthGrid.Build(2026, month);

            Assert.Equal(42, cells.Count);
            var days = DateTime.DaysInMonth(2026, month);
            foreach (var day in Enumerable.Range(1, days))
                Assert.Contains(new DateOnly(2026, month, day), cells);
        }
    }

    [Fact]
    public void Build_RunsContinuouslyWithNoGapsOrRepeats()
    {
        var cells = MonthGrid.Build(2026, 8);

        for (var i = 1; i < cells.Count; i++)
            Assert.Equal(cells[i - 1].AddDays(1), cells[i]);
    }

    [Fact]
    public void Headings_MatchTheOrderDaysAreLaidOutIn()
    {
        var headings = MonthGrid.Headings();
        var cells = MonthGrid.Build(2026, 8);

        Assert.Equal(7, headings.Count);
        for (var i = 0; i < 7; i++)
            Assert.Equal(cells[i].DayOfWeek, headings[i]);
    }

    [Fact]
    public void AWeekCanStartOnMonday()
    {
        var cells = MonthGrid.Build(2026, 8, DayOfWeek.Monday);

        Assert.Equal(DayOfWeek.Monday, cells[0].DayOfWeek);
        Assert.Equal(new DateOnly(2026, 7, 27), cells[0]);
        Assert.Equal(DayOfWeek.Monday, MonthGrid.Headings(DayOfWeek.Monday)[0]);
    }
}
