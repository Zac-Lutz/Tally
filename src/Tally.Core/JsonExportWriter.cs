using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// Serializes a day's sessions to the Suggestion Export schema_version "2" format that the att
/// timesheet imports. A slot is one proposed timesheet entry, built by
/// <see cref="SuggestionSlotBuilder"/> — a billing target (ticket, else category) within one
/// working session, with the hours it earned.
/// <para>
/// The importer validates the whole document and rejects it entirely on any field error, so every
/// contract bound is enforced here rather than hoped for: caps are truncated, not left to fail on
/// upload. Calls and manual timers overlay the day's hours rather than adding to it, so they
/// appear as <c>evidence</c> on the slots they overlap, never as slots of their own. Fields Tally
/// has no data for are honestly empty: <c>browser</c> (no URL capture) and <c>sessions</c> (no
/// structured repo/branch).
/// </para>
/// </summary>
public static class JsonExportWriter
{
    // Default encoder escapes < > & to \u00xx, which keeps the output safe to embed inside an
    // HTML <script> block (no "</script>" can appear).
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // The consumer's contract bounds (att SuggestionImportService). Exceeding any one of these
    // rejects the ENTIRE document, so the writer stays inside them by construction.
    private const int MaxSlotIdLength = 64;
    private const int MaxBucketLength = 200;
    private const int MaxNoteLength = 500;
    private const int MaxSummaryLength = 400;
    private const int MaxEvidenceItems = 20;
    private const int MaxEvidenceItemLength = 64;
    private const int MaxItems = 20;
    private const int MaxItemRefLength = 64;
    private const int MaxItemTitleLength = 200;
    private const int MaxWindowTitles = 10;
    private const int MaxWindowTitleLength = 160;
    private const int MaxMachineNameLength = 64;

    public static string BuildJson(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        JsonExportContext context,
        IReadOnlyList<ManualTimer>? timers = null,
        SuggestionSlotOptions? slotOptions = null)
        => BuildJson(date, BuildEntries(blocks, calls, timers, slotOptions), calls, timers ?? [], context);

    /// <summary>
    /// The export's entries in editable form — what the export dialog previews and lets the user
    /// rewrite before <see cref="BuildJson(DateOnly, IReadOnlyList{ExportEntry}, IReadOnlyList{CallSpan}, IReadOnlyList{ManualTimer}, JsonExportContext)"/>
    /// serializes them. An unedited round-trip produces the same document the one-shot overload does.
    /// </summary>
    public static IReadOnlyList<ExportEntry> BuildEntries(
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer>? timers = null,
        SuggestionSlotOptions? slotOptions = null)
        => SuggestionSlotBuilder.Build(blocks, calls, timers, slotOptions).Select(ExportEntry.From).ToList();

    /// <summary>Serializes entries — edited or not — enforcing every contract bound again, so a
    /// hand-typed note or ticket can't produce a document the importer rejects.</summary>
    public static string BuildJson(
        DateOnly date,
        IReadOnlyList<ExportEntry> entries,
        IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers,
        JsonExportContext context)
    {
        var slots = BuildSlots(entries, calls, timers, context.Machine);
        var rangeStart = slots.Count > 0 ? slots[0].Date : date.ToString("yyyy-MM-dd");
        var rangeEnd = slots.Count > 0 ? slots[^1].Date : date.ToString("yyyy-MM-dd");

        var export = new JsonExport(
            "2",
            new JsonSource(context.Producer, Cap(context.Machine, MaxMachineNameLength), Iso(context.GeneratedAt)),
            new JsonRange(rangeStart, rangeEnd),
            slots);

        return JsonSerializer.Serialize(export, Options);
    }

    private static List<JsonSlot> BuildSlots(
        IReadOnlyList<ExportEntry> entries, IReadOnlyList<CallSpan> calls,
        IReadOnlyList<ManualTimer> timers, string machine)
    {
        // Slot ids must be unique across the document or the import is rejected. The natural id
        // (start minute + bucket) collides when a target is returned to inside the same minute, so
        // uniqueness is enforced here rather than assumed.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        return entries.Select(e => BuildSlot(e, calls, timers, machine, taken)).ToList();
    }

    private static JsonSlot BuildSlot(
        ExportEntry entry, IReadOnlyList<CallSpan> calls, IReadOnlyList<ManualTimer> timers,
        string machine, HashSet<string> takenIds)
    {
        var slot = entry.Slot;
        var start = slot.Start.ToLocalTime();
        var end = slot.End.ToLocalTime();
        var bucket = Bucket(slot.Category);
        var items = BuildItems(entry);
        var note = CapLines(entry.Note, MaxNoteLength);

        return new JsonSlot(
            Id: UniqueId(start, bucket, takenIds),
            Date: start.ToString("yyyy-MM-dd"),
            Start: Iso(start),
            End: Iso(end),
            // The entry's hours — the reported (rounded) figure unless the reviewer changed it.
            Hours: Hours(entry.Hours),
            Bucket: Cap(bucket, MaxBucketLength),
            Note: note,
            Evidence: BuildEvidence(slot, calls, timers),
            WorkingToward: false,
            // The consumer default-checks the summary when there is one, otherwise every work
            // item. An activity slot with a ticket therefore omits the summary — letting the
            // ticket compose the note beats a sentence that buries it. A call or timer always
            // supplies one: its own name is the point, and the tickets that happened to be on
            // screen underneath it must not outrank the meeting in the note.
            Summary: items.Count > 0 && slot.Kind == SuggestionSlotKind.Activity
                ? null
                : CapLines(note, MaxSummaryLength),
            Items: items,
            WindowTitles: BuildWindowTitles(slot),
            Browser: [],     // URLs are captured but not exported yet (phase 3 of URL capture)
            Sessions: [],    // no structured repo/branch capture
            Machines: [new JsonMachine(Cap(machine, MaxMachineNameLength), Seconds(slot.Measured.TotalSeconds))]);
    }

    // The entry's work items. An untouched ticket keeps the slot's own list (every ticket its
    // blocks named, biggest first); a reviewer's edit replaces that with exactly what they typed —
    // one item, or none when they cleared it.
    private static List<JsonItem> BuildItems(ExportEntry entry)
    {
        if (!string.Equals(entry.Ticket, entry.Slot.TicketRef, StringComparison.Ordinal))
        {
            return entry.Ticket is { } ticket
                ?
                [
                    new JsonItem(
                        "wi",
                        Cap("#" + ticket, MaxItemRefLength),
                        Cap(entry.Title.Length > 0 ? entry.Title : $"Ticket #{ticket}", MaxItemTitleLength),
                        string.Empty),
                ]
                : [];
        }

        return entry.Slot.Blocks
            .Where(b => b.EffectiveTicket is not null)
            .GroupBy(b => b.EffectiveTicket!)
            .OrderByDescending(g => g.Sum(b => b.Block.Duration.Ticks))
            .Take(MaxItems)
            // title is required by the contract, so fall back to the ticket itself when every
            // window for it had a blank title.
            .Select(g => new JsonItem(
                "wi",
                Cap("#" + g.Key, MaxItemRefLength),
                Cap(FirstNonEmptyTitle(g) ?? $"Ticket #{g.Key}", MaxItemTitleLength),
                string.Empty))
            .ToList();
    }

    private static string? FirstNonEmptyTitle(IEnumerable<ClassifiedBlock> blocks)
        => blocks
            .OrderByDescending(b => b.Block.Duration)
            .Select(b => TitleNormalizer.Normalize(b.Block.Title))
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    // Window titles are contract-required to be non-empty, and Explorer reports blank ones, so
    // blanks are dropped rather than allowed to reject the document. Only the ten biggest fit.
    private static List<JsonWindowTitle> BuildWindowTitles(SuggestionSlot slot)
        => slot.Blocks
            .Select(b => (Title: TitleNormalizer.Normalize(b.Block.Title), b.Block.Duration))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => x.Title, StringComparer.Ordinal)
            .Select(g => new JsonWindowTitle(Cap(g.Key, MaxWindowTitleLength), Seconds(g.Sum(x => x.Duration.TotalSeconds))))
            .Where(w => w.Seconds > 0)
            .OrderByDescending(w => w.Seconds)
            .Take(MaxWindowTitles)
            .ToList();

    private static List<string> BuildEvidence(
        SuggestionSlot slot, IReadOnlyList<CallSpan> calls, IReadOnlyList<ManualTimer> timers)
    {
        var evidence = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string line)
        {
            if (evidence.Count >= MaxEvidenceItems)
                return;
            var capped = Cap(line, MaxEvidenceItemLength);
            if (capped.Length > 0 && seen.Add(capped))
                evidence.Add(capped);
        }

        foreach (var ticket in slot.Blocks.Select(b => b.EffectiveTicket).Where(t => t is not null).Distinct())
            Add($"Ticket #{ticket}");

        foreach (var subject in slot.Blocks.Select(b => b.Classification.Subject).Where(s => s is { Length: > 0 }).Distinct())
            Add($"{slot.Category}: {subject}");

        // A call or timer whose span overlaps this slot is evidence of what the time was.
        foreach (var call in calls.Where(c => c.Start < slot.End && c.End > slot.Start))
            Add($"Call: {(call.Title.Length > 0 ? call.Title : call.ProcessName)}");

        foreach (var timer in timers.Where(t => t.Start < slot.End && t.End > slot.Start))
            Add($"Timer: {timer.Name} ({ReportFormat.Duration(timer.Duration)})");

        return evidence;
    }

    /// <summary>
    /// A slot id the importer accepts: its charset is letters, digits, '-', '_' and '.', it's
    /// capped, and it's unique within the document. Ids are stable across exports of the same day
    /// (the consumer uses them to keep already-logged suggestions marked as added), so the natural
    /// start-minute + bucket form is kept and only collisions take a suffix.
    /// </summary>
    private static string UniqueId(DateTimeOffset start, string bucket, HashSet<string> taken)
    {
        var stem = Cap($"{start:yyyy-MM-dd'T'HHmm}-{bucket}", MaxSlotIdLength - 4);
        if (taken.Add(stem))
            return stem;

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}";
            if (taken.Add(candidate))
                return candidate;
        }
    }

    // A bucket doubles as part of the slot id, whose charset is strict — and categories are
    // free text now that they can be typed into the triage tab, so anything else is folded away.
    private static string Bucket(string category)
    {
        var slug = new string(category.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length > 0 ? slug : "other";
    }

    /// <summary>Hours as the contract wants them: at most two decimals, and never zero (a slot
    /// that reports no time is rejected, and rounding a real activity to nothing loses work).</summary>
    private static double Hours(double hours)
        => Math.Max(0.01, Math.Round(hours, 2));

    private static int Seconds(double seconds) => (int)Math.Round(seconds);

    private static string Cap(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    /// <summary>
    /// Caps a note that is one activity per line by dropping whole lines from the end, so a note
    /// too long for the contract loses its least-important line rather than ending mid-word. A
    /// single line longer than the bound is still truncated — there is nothing else to give up.
    /// </summary>
    private static string CapLines(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var lines = value.Split('\n');
        var kept = new List<string>();
        var length = 0;
        foreach (var line in lines)
        {
            var cost = kept.Count == 0 ? line.Length : line.Length + 1;
            if (length + cost > maxLength)
                break;
            kept.Add(line);
            length += cost;
        }

        return kept.Count > 0 ? string.Join('\n', kept) : Cap(lines[0], maxLength);
    }

    private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    // DTOs — property declaration order is the JSON key order; SnakeCaseLower maps the names.
    internal sealed record JsonExport(string SchemaVersion, JsonSource Source, JsonRange Range, IReadOnlyList<JsonSlot> Slots);

    internal sealed record JsonSource(string Producer, string Machine, string GeneratedAt);

    internal sealed record JsonRange(string Start, string End);

    internal sealed record JsonSlot(
        string Id, string Date, string Start, string End, double Hours, string Bucket,
        string Note, IReadOnlyList<string> Evidence, bool WorkingToward, string? Summary,
        IReadOnlyList<JsonItem> Items, IReadOnlyList<JsonWindowTitle> WindowTitles,
        IReadOnlyList<JsonBrowser> Browser, IReadOnlyList<JsonSession> Sessions, IReadOnlyList<JsonMachine> Machines);

    internal sealed record JsonItem(string Kind, string Ref, string Title, string Description);

    internal sealed record JsonWindowTitle(string Title, int Seconds);

    internal sealed record JsonBrowser(string Title, string Url, int Visits);

    internal sealed record JsonSession(string Repo, string Branch);

    internal sealed record JsonMachine(string Machine, int Seconds);
}
