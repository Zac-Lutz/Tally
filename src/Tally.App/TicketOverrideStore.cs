using System.Text.Json;

namespace Tally.App;

/// <summary>
/// Per-day manual ticket numbers the user types into the Rollup's Ticket cells, persisted to
/// ticket-overrides.json as { "yyyy-MM-dd": { rowKey: ticket } }. The rowKey is the stable activity
/// identity from <see cref="Tally.Core.TicketOverrideKey"/>. Read during report generation, written
/// from the live view — a tiny file, so reads/writes just take a lock and re-read the whole thing.
/// </summary>
public static class TicketOverrideStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly IReadOnlyDictionary<string, string> EmptyDay = new Dictionary<string, string>();

    /// <summary>The rowKey -> ticket overrides recorded for <paramref name="date"/> (empty if none).</summary>
    public static IReadOnlyDictionary<string, string> GetForDate(DateOnly date)
    {
        lock (Gate)
        {
            var all = Load();
            return all.TryGetValue(Key(date), out var day) ? day : EmptyDay;
        }
    }

    /// <summary>Records the manual ticket for one activity on one day; a blank value clears it.</summary>
    public static void Set(DateOnly date, string rowKey, string? ticket)
    {
        lock (Gate)
        {
            var all = Load();
            var dayKey = Key(date);
            if (!all.TryGetValue(dayKey, out var day))
            {
                day = new Dictionary<string, string>();
                all[dayKey] = day;
            }

            var trimmed = ticket?.Trim().TrimStart('#').Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                day.Remove(rowKey);
                if (day.Count == 0)
                    all.Remove(dayKey);
            }
            else
            {
                day[rowKey] = trimmed;
            }

            Save(all);
        }
    }

    private static string Key(DateOnly date) => date.ToString("yyyy-MM-dd");

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            if (!File.Exists(TallyPaths.TicketOverridesPath))
                return new();
            var json = File.ReadAllText(TallyPaths.TicketOverridesPath);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read ticket-overrides.json — treating as empty", ex);
            return new();
        }
    }

    private static void Save(Dictionary<string, Dictionary<string, string>> all)
    {
        try
        {
            TallyPaths.EnsureCreated();
            File.WriteAllText(TallyPaths.TicketOverridesPath, JsonSerializer.Serialize(all, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to write ticket-overrides.json", ex);
        }
    }
}
