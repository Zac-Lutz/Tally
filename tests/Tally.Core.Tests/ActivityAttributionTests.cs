using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class ActivityAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ActivitySample Sample(double minutes, int keys, int clicks)
        => new() { Timestamp = T0.AddMinutes(minutes), Keystrokes = keys, MouseClicks = clicks };

    [Fact]
    public void SamplesWithinBlock_AreSummed_OutsideAreIgnored()
    {
        var block = new Block(T0, T0.AddMinutes(10), "chrome", "x");
        var activity = ActivityAttribution.For(block,
        [
            Sample(1, 100, 10),
            Sample(5, 50, 5),
            Sample(20, 999, 999),   // outside the block
        ]);

        Assert.Equal(150, activity.Keystrokes);
        Assert.Equal(15, activity.MouseClicks);
    }

    [Fact]
    public void BlockEnd_IsExclusive_StartIsInclusive()
    {
        var block = new Block(T0, T0.AddMinutes(10), "chrome", "x");
        var activity = ActivityAttribution.For(block,
        [
            Sample(0, 1, 0),    // exactly Start — included
            Sample(10, 0, 1),   // exactly End — excluded (belongs to the next block)
        ]);

        Assert.Equal(1, activity.Keystrokes);
        Assert.Equal(0, activity.MouseClicks);
    }

    [Fact]
    public void NoSamples_ReturnsNoneInstance()
    {
        var block = new Block(T0, T0.AddMinutes(10), "chrome", "x");

        Assert.Same(BlockActivity.None, ActivityAttribution.For(block, []));
    }
}
