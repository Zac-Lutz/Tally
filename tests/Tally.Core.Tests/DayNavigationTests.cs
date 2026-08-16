using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

public class DayNavigationTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);       // a Sunday
    private static readonly DateOnly Earliest = new(2026, 6, 1);

    [Fact]
    public void Clamp_RefusesToLookPastToday()
        => Assert.Equal(Today, DayNavigation.Clamp(Today.AddDays(3), Earliest, Today));

    [Fact]
    public void Clamp_StopsAtTheFirstDayOnRecord()
        => Assert.Equal(Earliest, DayNavigation.Clamp(Earliest.AddDays(-30), Earliest, Today));

    [Fact]
    public void Clamp_LeavesADayInsideTheRangeAlone()
    {
        var day = new DateOnly(2026, 7, 4);

        Assert.Equal(day, DayNavigation.Clamp(day, Earliest, Today));
    }

    [Fact]
    public void Clamp_CollapsesToToday_WhenNothingIsOnRecordYet()
    {
        // An empty database reports its earliest day as today; a range of one day is still a range.
        Assert.Equal(Today, DayNavigation.Clamp(Today.AddDays(-5), Today, Today));
    }

    [Fact]
    public void Clamp_SurvivesAnEarliestAfterToday()
    {
        // A clock that moved backwards would otherwise make the floor higher than the ceiling.
        Assert.Equal(Today, DayNavigation.Clamp(Today, Today.AddDays(4), Today));
    }

    [Fact]
    public void CanGoBack_IsFalseOnTheFirstDayOnRecord()
    {
        Assert.False(DayNavigation.CanGoBack(Earliest, Earliest, Today));
        Assert.True(DayNavigation.CanGoBack(Earliest.AddDays(1), Earliest, Today));
    }

    [Fact]
    public void CanGoForward_IsFalseOnToday()
    {
        Assert.False(DayNavigation.CanGoForward(Today, Today));
        Assert.True(DayNavigation.CanGoForward(Today.AddDays(-1), Today));
    }

    [Fact]
    public void Step_StopsAtTheEndsInsteadOfWalkingPastThem()
    {
        Assert.Equal(Today, DayNavigation.Step(Today, +1, Earliest, Today));
        Assert.Equal(Earliest, DayNavigation.Step(Earliest, -1, Earliest, Today));
        Assert.Equal(Today.AddDays(-1), DayNavigation.Step(Today, -1, Earliest, Today));
    }

    [Fact]
    public void Label_NamesTodayAndYesterdayBeforeItNamesTheWeekday()
    {
        Assert.Equal("Today · 08-16-2026", DayNavigation.Label(Today, Today));
        Assert.Equal("Yesterday · 08-15-2026", DayNavigation.Label(Today.AddDays(-1), Today));
        Assert.Equal("Friday · 08-14-2026", DayNavigation.Label(Today.AddDays(-2), Today));
    }
}
