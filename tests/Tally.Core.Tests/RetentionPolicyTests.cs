using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

public class RetentionPolicyTests
{
    private static readonly DateOnly Today = new(2026, 8, 13);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegative_MeansKeepForever(int days)
        => Assert.Null(RetentionPolicy.Cutoff(Today, days));

    [Fact]
    public void Cutoff_IsLocalMidnight_WholeDaysBack()
    {
        var cutoff = RetentionPolicy.Cutoff(Today, 90);

        Assert.Equal(new DateTimeOffset(new DateTime(2026, 5, 15)), cutoff);
        Assert.Equal(TimeSpan.Zero, cutoff!.Value.TimeOfDay);
    }

    [Fact]
    public void PositiveBelowMinimum_IsRaisedToTheFloor()
        => Assert.Equal(
            RetentionPolicy.Cutoff(Today, RetentionPolicy.MinimumDays),
            RetentionPolicy.Cutoff(Today, 1));

    [Fact]
    public void CutoffDay_ItselfSurvives()
    {
        // Retention of 7 keeps today plus the 7 days before it; only day 8 and older go.
        var cutoff = RetentionPolicy.Cutoff(Today, 7)!.Value;
        var oldestKeptDay = new DateTimeOffset(new DateTime(2026, 8, 6));

        Assert.Equal(oldestKeptDay, cutoff);
    }
}
