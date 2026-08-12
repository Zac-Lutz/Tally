using Tally.Core.Models;

namespace Tally.Core;

/// <summary>Attributes activity samples to foreground blocks by timestamp containment.</summary>
public static class ActivityAttribution
{
    /// <summary>
    /// Sums the samples whose timestamp falls within <paramref name="block"/> (<c>[Start, End)</c>).
    /// Each sample covers the interval ending at its timestamp and is credited whole to the block
    /// containing that timestamp — a rough intensity signal; a sample straddling a block boundary
    /// is slightly misattributed, which is acceptable at ~1-minute granularity.
    /// </summary>
    public static BlockActivity For(Block block, IReadOnlyList<ActivitySample> samples)
    {
        var keys = 0;
        var clicks = 0;
        foreach (var sample in samples)
        {
            if (sample.Timestamp >= block.Start && sample.Timestamp < block.End)
            {
                keys += sample.Keystrokes;
                clicks += sample.MouseClicks;
            }
        }

        return keys == 0 && clicks == 0 ? BlockActivity.None : new BlockActivity(keys, clicks);
    }
}
