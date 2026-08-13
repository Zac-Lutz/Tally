using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// Serializes a day's sessions to the schema_version "2" export format. A "slot" is a run of
/// consecutive same-category blocks; its hours are the summed active time (not wall-clock, so
/// idle gaps inside the run are excluded). Fields Tally has no data for are honestly empty:
/// <c>browser</c> (no URL capture), <c>sessions</c> (no structured repo/branch), and
/// <c>summary</c> is omitted entirely (never emitted as null).
/// </summary>
public static class JsonExportWriter
{
    // Default encoder escapes &lt; &gt; &amp; to \u00xx, which keeps the output safe to embed
    // inside an HTML <script> block (no "</script>" can appear).
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string BuildJson(
        DateOnly date,
        IReadOnlyList<ClassifiedBlock> blocks,
        IReadOnlyList<CallSpan> calls,
        JsonExportContext context)
    {
        var slots = BuildSlots(blocks, calls, context.Machine);
        var rangeStart = slots.Count > 0 ? slots[0].Date : date.ToString("yyyy-MM-dd");
        var rangeEnd = slots.Count > 0 ? slots[^1].Date : date.ToString("yyyy-MM-dd");

        var export = new JsonExport(
            "2",
            new JsonSource(context.Producer, context.Machine, Iso(context.GeneratedAt)),
            new JsonRange(rangeStart, rangeEnd),
            slots);

        return JsonSerializer.Serialize(export, Options);
    }

    private static List<JsonSlot> BuildSlots(
        IReadOnlyList<ClassifiedBlock> blocks, IReadOnlyList<CallSpan> calls, string machine)
    {
        var slots = new List<JsonSlot>();
        var i = 0;
        while (i < blocks.Count)
        {
            var category = blocks[i].Classification.Category;
            var group = new List<ClassifiedBlock>();
            while (i < blocks.Count && blocks[i].Classification.Category == category)
            {
                group.Add(blocks[i]);
                i++;
            }

            slots.Add(BuildSlot(category, group, calls, machine));
        }

        return slots;
    }

    private static JsonSlot BuildSlot(
        string category, List<ClassifiedBlock> group, IReadOnlyList<CallSpan> calls, string machine)
    {
        var startUtc = group[0].Block.Start;
        var endUtc = group[^1].Block.End;
        var start = startUtc.ToLocalTime();
        var activeSeconds = group.Sum(g => g.Block.Duration.TotalSeconds);
        var bucket = Slug(category);

        var windowTitles = group
            .GroupBy(g => g.Block.Title)
            .Select(t => new JsonWindowTitle(t.Key, Seconds(t.Sum(x => x.Block.Duration.TotalSeconds))))
            .Where(w => w.Seconds > 0)
            .OrderByDescending(w => w.Seconds)
            .ToList();

        var items = group
            .Where(g => g.EffectiveTicket is not null)
            .Select(g => g.EffectiveTicket!)
            .Distinct()
            .Select(ticket => new JsonItem("wi", "#" + ticket, TicketTitle(group, ticket), string.Empty))
            .ToList();

        return new JsonSlot(
            Id: $"{start:yyyy-MM-dd'T'HHmm}-{bucket}",
            Date: start.ToString("yyyy-MM-dd"),
            Start: Iso(start),
            End: Iso(endUtc.ToLocalTime()),
            Hours: Math.Round(activeSeconds / 3600.0, 2),
            Bucket: bucket,
            Note: string.Empty,
            Evidence: BuildEvidence(group, calls, startUtc, endUtc),
            WorkingToward: false,
            Summary: null,   // Tally has no per-slot summary; omitted from the output entirely
            Items: items,
            WindowTitles: windowTitles,
            Browser: [],     // no URL capture
            Sessions: [],    // no structured repo/branch capture
            Machines: [new JsonMachine(machine, Seconds(activeSeconds))]);
    }

    private static List<string> BuildEvidence(
        List<ClassifiedBlock> group, IReadOnlyList<CallSpan> calls, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var evidence = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string line)
        {
            if (seen.Add(line))
                evidence.Add(line);
        }

        foreach (var ticket in group.Select(g => g.EffectiveTicket).Where(t => t is not null))
            Add($"Ticket #{ticket}");

        foreach (var g in group)
            if (g.Classification.Subject is { Length: > 0 } subject)
                Add($"{g.Classification.Category}: {subject}");

        // A call whose mic span overlaps this slot's time range is meeting evidence.
        foreach (var call in calls.Where(c => c.Start < endUtc && c.End > startUtc))
            Add($"Call: {(call.Title.Length > 0 ? call.Title : call.ProcessName)}");

        return evidence;
    }

    private static string TicketTitle(List<ClassifiedBlock> group, string ticket)
        => group.First(g => g.EffectiveTicket == ticket).Block.Title;

    private static int Seconds(double seconds) => (int)Math.Round(seconds);

    private static string Slug(string category) => category.Trim().ToLowerInvariant().Replace(' ', '-');

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
