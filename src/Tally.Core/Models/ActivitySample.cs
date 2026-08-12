namespace Tally.Core.Models;

/// <summary>
/// Aggregate input counts over one flush interval (~1 min), timestamped at the interval end.
/// Records COUNTS of keystrokes and mouse clicks only — never key identity or content — so the
/// database can never become a keystroke log. Attributed to foreground blocks at report time.
/// </summary>
public sealed class ActivitySample
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int Keystrokes { get; set; }
    public int MouseClicks { get; set; }
}
