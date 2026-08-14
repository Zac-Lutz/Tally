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
            Title = slot.Label,
            Note = string.Empty,
            Ticket = slot.TicketRef,
            Hours = Math.Round(slot.Reported.TotalHours, 2),
        }.WithComposedNote();

    /// <summary>
    /// The entry with its note recomposed from the current title and ticket — the default text an
    /// unedited entry exports with, and what an edited title falls back to until the note itself
    /// is hand-edited.
    /// </summary>
    public ExportEntry WithComposedNote() => this with { Note = Compose() };

    private string Compose() => Slot.Kind switch
    {
        SuggestionSlotKind.OddsAndEnds =>
            $"Odds and ends — {DistinctActivities()} short activities, none long enough to stand alone",
        SuggestionSlotKind.Call => $"Call - {Title}",
        SuggestionSlotKind.Timer => $"Timer - {Title}",
        _ when Ticket is { } ticket => $"Ticket #{ticket} - {Title}",
        _ => $"{Slot.Category} - {Title}",
    };

    private int DistinctActivities()
        => Slot.Blocks
            .Select(b => TitleNormalizer.Normalize(b.Block.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
}
