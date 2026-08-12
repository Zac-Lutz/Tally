namespace Tally.Core.Models;

/// <summary>A completed manual timer — a user-declared, explicitly named span of time.</summary>
public sealed class ManualTimer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }

    public TimeSpan Duration => End - Start;
}
