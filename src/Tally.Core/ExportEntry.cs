namespace Tally.Core;

/// <summary>
/// One export entry as the person reviewing it sees it — the editable surface of a slot. The
/// underlying <see cref="Slot"/> (its times, blocks, evidence) stays measured fact; what can be
/// edited before export is the story told about it: the <see cref="Title"/>, the full
/// <see cref="Note"/> text, the <see cref="Ticket"/> it books to, and the <see cref="Hours"/>.
/// </summary>
public sealed record ExportEntry
{
    public required SuggestionSlot Slot { get; init; }

    /// <summary>The entry's short name — the label part the note is composed around.</summary>
    public required string Title { get; init; }

    /// <summary>The full note text the file carries. Composed from the title and ticket by
    /// default; editable wholesale.</summary>
    public required string Note { get; init; }

    /// <summary>The work item the entry books to; null books to the bucket alone.</summary>
    public string? Ticket { get; init; }

    /// <summary>Hours to enter, as the timesheet will book them.</summary>
    public required double Hours { get; init; }

    public static ExportEntry From(SuggestionSlot slot)
        => new ExportEntry
        {
            Slot = slot,
            // The category is the entry's name. What it was *specifically* is the note's job, and
            // saying it twice only left the reader deciding which half to read.
            Title = slot.Category,
            Note = string.Empty,
            Ticket = slot.TicketRef,
            Hours = Math.Round(slot.Reported.TotalHours, 2),
        }.WithComposedNote();

    /// <summary>
    /// The entry with its note recomposed from the slot — the default text an unedited entry
    /// exports with, and what an edit falls back to until the note itself is hand-edited.
    /// </summary>
    public ExportEntry WithComposedNote() => this with { Note = Compose() };

    /// <summary>
    /// The note: what the time was actually spent on, one activity per line, longest first.
    /// It carries no category, ticket or hours — every one of those is already its own field on
    /// the entry, and repeating them here just buried the only thing this field can say.
    /// </summary>
    private string Compose()
    {
        var lines = new List<string>();

        // A call or a timer names itself — the meeting or the declared task IS what the time was,
        // and the windows underneath only describe what was on screen during it.
        if (Slot.Kind is SuggestionSlotKind.Call or SuggestionSlotKind.Timer)
            lines.Add(Slot.Label);

        lines.AddRange(Activities());

        // A slot whose windows were all untitled still has to say something.
        if (lines.Count == 0)
            lines.Add(Slot.Label);

        return string.Join('\n', lines.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The distinct activities the slot's time went to, longest first. Anything under a minute is
    /// left out — the same threshold the Rollup uses to keep a glance from becoming a list — but
    /// never all of them: when every activity is that brief, the largest still stands for the slot
    /// rather than leaving it described by nothing.
    /// </summary>
    private IEnumerable<string> Activities()
    {
        var activities = Slot.Blocks
            .Select(b => (Title: TitleNormalizer.Normalize(b.Block.Title), b.Block.Duration))
            .Where(a => !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Title: g.Key, Time: TimeSpan.FromTicks(g.Sum(a => a.Duration.Ticks))))
            .OrderByDescending(a => a.Time)
            .ToList();

        var worthNaming = activities.Where(a => a.Time >= RollupBuilder.MinRollupDuration).ToList();
        if (worthNaming.Count == 0)
            worthNaming = [.. activities.Take(1)];

        return worthNaming.Select(a => a.Title);
    }
}
