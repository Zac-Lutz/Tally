using Tally.Core.Models;

namespace Tally.Core;

/// <summary>
/// One aggregated rollup line: a category + specific activity with its summed time.
/// <see cref="TicketRef"/> is the effective ticket to display (a manual override wins over the
/// auto-detected one). <see cref="RowKey"/> is the stable identity for a per-day manual ticket
/// override (null = not editable, e.g. call rows).
/// </summary>
public sealed record RollupRow(
    string Category, string? Client, string? TicketRef, string DetailName, TimeSpan Time,
    string? RowKey = null);

/// <summary>
/// Builds the report rollup at per-activity granularity. Each distinct activity gets its own row:
/// a Teams chat (by subject), a Halo ticket (by ticket number, so differing titles still merge),
/// or otherwise an individual window/browser tab (by normalized title). Rows are summed across
/// the whole day and ordered by time spent.
/// </summary>
public static class RollupBuilder
{
    // Grouping is by the ORIGINAL classification (category, client, auto-ticket, activity), so a
    // manual override never re-groups a row. The displayed ticket is the effective one (override
    // wins); the RowKey is built from the original ticket so it stays put once a value is entered.
    public static IReadOnlyList<RollupRow> Build(IReadOnlyList<ClassifiedBlock> blocks)
        => blocks
            .GroupBy(b => (b.Classification.Category, b.Classification.Client, b.Classification.TicketRef, Key: ActivityKey(b)))
            .Select(g =>
            {
                var detail = DisplayName(g);
                var overrideTicket = g.Select(x => x.OverrideTicket).FirstOrDefault(o => o is not null);
                return new RollupRow(
                    g.Key.Category,
                    g.Key.Client,
                    overrideTicket ?? g.Key.TicketRef,
                    detail,
                    TimeSpan.FromTicks(g.Sum(x => x.Block.Duration.Ticks)),
                    TicketOverrideKey.ForRow(g.Key.Category, g.Key.TicketRef, detail));
            })
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Rollup rows for the day's calls, so call time shows in the Rollup alongside window activity
    /// (not only in the Calls tab). Each distinct call target — the app, plus its window title when
    /// that adds detail — is one row under the "Call" category, summed across the day. Calls overlay
    /// foreground blocks rather than replacing them, so these rows are additive: the same minute can
    /// appear both as a call and as whatever window was focused.
    /// </summary>
    public static IReadOnlyList<RollupRow> BuildCalls(
        IReadOnlyList<CallSpan> calls, IReadOnlyDictionary<string, string>? ticketOverrides = null)
        => calls
            .Select(c => (Label: CallLabel(c), c.Duration))
            .GroupBy(x => x.Label)
            .Select(g =>
            {
                // Calls carry a per-day manual ticket like activity rows do — keyed by the call
                // target (app + name) so a ticket typed on a call re-applies on recompute.
                var activity = g.Key.Client is { } client ? $"{client} / {g.Key.Name}" : g.Key.Name;
                var rowKey = TicketOverrideKey.ForRow(CallCategory, null, activity);
                var ticket = ticketOverrides is not null && ticketOverrides.TryGetValue(rowKey, out var t) ? t : null;
                return new RollupRow(
                    CallCategory, g.Key.Client, ticket, g.Key.Name,
                    TimeSpan.FromTicks(g.Sum(x => x.Duration.Ticks)),
                    rowKey);
            })
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Rollup rows for the day's manual timers, so a named timer shows in the Rollup under the
    /// "Timer" category with its name as the detail (timers sharing a name are summed into one row).
    /// Not ticket-editable there (RowKey null); rename a timer in the Timers tab and it reflects here.
    /// </summary>
    public static IReadOnlyList<RollupRow> BuildTimers(IReadOnlyList<ManualTimer> timers)
        => timers
            .GroupBy(t => t.Name)
            .Select(g => new RollupRow(
                TimerCategory, null, null, g.Key,
                TimeSpan.FromTicks(g.Sum(t => t.Duration.Ticks))))
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.DetailName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The category label calls carry in the rollup (and its badge color in the writers).</summary>
    public const string CallCategory = "Call";

    /// <summary>The category label manual timers carry in the rollup.</summary>
    public const string TimerCategory = "Timer";

    /// <summary>Rollup rows shorter than this are hidden as noise. The time still counts in the
    /// summary totals, the Timeline, and the JSON export — only the Rollup table drops them.</summary>
    public static readonly TimeSpan MinRollupDuration = TimeSpan.FromMinutes(1);

    // A call's rollup label: the app on its own when the title adds nothing (empty, or just the app
    // name again), otherwise app + the cleaned window title (e.g. a Teams meeting or Discord channel).
    private static (string? Client, string Name) CallLabel(CallSpan c)
    {
        var title = TitleNormalizer.Normalize(c.Title);
        return string.IsNullOrWhiteSpace(title) || string.Equals(title, c.ProcessName, StringComparison.OrdinalIgnoreCase)
            ? (null, c.ProcessName)
            : (c.ProcessName, title);
    }

    // The within-(category, client, ticket) grouping key. Null for ticketed blocks so all views of
    // one ticket merge regardless of title; the subject for Teams chats; else the per-tab title.
    private static string? ActivityKey(ClassifiedBlock b)
        => b.Classification.Subject
           ?? (b.Classification.TicketRef is not null ? null : TitleNormalizer.Normalize(b.Block.Title));

    private static string DisplayName(IEnumerable<ClassifiedBlock> group)
    {
        var blocks = group.ToList();
        var subject = blocks.Select(b => b.Classification.Subject).FirstOrDefault(s => s is not null);
        if (subject is not null)
            return subject;

        // Represent the row by the normalized title of its longest-running block.
        var representative = blocks.OrderByDescending(b => b.Block.Duration).First().Block.Title;
        return TitleNormalizer.Normalize(representative);
    }
}
