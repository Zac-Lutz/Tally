using Microsoft.EntityFrameworkCore;
using Tally.Core;
using Tally.Core.Models;

namespace Tally.App;

/// <summary>A day's classified sessions, ready to render into any report format or the live view.</summary>
public sealed record ReportData(
    DateOnly Date,
    IReadOnlyList<ClassifiedBlock> Blocks,
    IReadOnlyList<CallSpan> Calls,
    IReadOnlyList<InactivePeriod> Inactive,
    IReadOnlyList<ManualTimer> Timers,
    IReadOnlyDictionary<string, string> TicketOverrides);

public static class ReportGenerator
{
    /// <summary>
    /// Loads and classifies a local calendar day's sessions from the database. Shared by the file
    /// report and the live view so both show identical data. For today, open blocks/calls run to
    /// "now"; past days clamp to the last recorded event.
    /// </summary>
    public static async Task<ReportData> ComputeAsync(DbContextOptions<TallyDbContext> dbOptions, DateOnly date)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));   // local midnight
        var dayEnd = dayStart.AddDays(1);

        List<TrackedEvent> events;
        List<ManualTimer> timers;
        await using (var db = new TallyDbContext(dbOptions))
        {
            TallyDbContext.EnsureSchema(db);
            events = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd)
                .OrderBy(e => e.Timestamp)
                .ToListAsync();
            timers = await db.ManualTimers.AsNoTracking()
                .Where(t => t.Start >= dayStart && t.Start < dayEnd)
                .ToListAsync();
        }

        var now = DateTimeOffset.Now;
        var endOfData = now < dayEnd
            ? now
            : events.Count > 0 ? events[^1].Timestamp : dayEnd;

        var sessions = Sessionizer.Build(events, endOfData);
        var classifier = new Classifier(LoadRules());
        var overrides = TicketOverrideStore.GetForDate(date);
        var classified = sessions.Blocks
            .Select(b =>
            {
                var classification = classifier.Classify(b.ProcessName, b.Title, b.Url);
                var key = TicketOverrideKey.ForBlock(
                    classification.Category, classification.TicketRef, classification.Subject, b.Title);
                var overrideTicket = overrides.TryGetValue(key, out var t) ? t : null;
                return new ClassifiedBlock(b, classification, overrideTicket);
            })
            .ToList();

        return new ReportData(date, classified, sessions.Calls, sessions.InactivePeriods, timers, overrides);
    }

    /// <summary>
    /// Builds the report for a local calendar day in <paramref name="format"/> and writes it to
    /// <paramref name="reportsDirectory"/>. Each run gets its own timestamped file
    /// (yyyy-MM-dd_HHmmss.&lt;ext&gt;, report date + run time), so successive runs never overwrite.
    /// </summary>
    public static async Task<string> GenerateAsync(
        DbContextOptions<TallyDbContext> dbOptions, DateOnly date, string reportsDirectory,
        ReportFileFormat format = ReportFileFormat.Html)
    {
        var data = await ComputeAsync(dbOptions, date);
        var content = format switch
        {
            ReportFileFormat.Markdown =>
                ReportWriter.BuildMarkdown(data.Date, data.Blocks, data.Calls, data.Inactive,
                    timers: data.Timers, ticketOverrides: data.TicketOverrides),
            ReportFileFormat.Json => BuildExportJson(data),
            // The snapshot carries its own export, so a saved report can be filed later without
            // the app running — the whole day embedded, with the range chosen in the page. The
            // user's category colours bake in, so the file matches what the live view showed.
            _ => HtmlReportWriter.BuildHtml(data.Date, data.Blocks, data.Calls, data.Inactive,
                timers: data.Timers, ticketOverrides: data.TicketOverrides,
                embeddedJson: BuildExportJson(data), palette: LoadPaletteSafe()),
        };

        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(reportsDirectory, $"{date:yyyy-MM-dd}_{DateTime.Now:HHmmss}.{format.Extension()}");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>
    /// The Suggestion Export for a computed day — the file the att timesheet imports. Shared by the
    /// headless <c>--report ... json</c> run and the live view's export button so both write the
    /// same document.
    /// </summary>
    public static string BuildExportJson(ReportData data, SuggestionSlotOptions? slotOptions = null)
        => JsonExportWriter.BuildJson(
            data.Date, data.Blocks, data.Calls,
            new JsonExportContext("tally", Environment.MachineName, DateTimeOffset.Now),
            data.Timers, slotOptions);

    /// <summary>The user's category definitions; a broken file reads as none rather than failing.</summary>
    internal static IReadOnlyList<CategoryDefinition> LoadCategoriesSafe()
    {
        try
        {
            return CategoriesFile.Load(TallyPaths.CategoriesPath);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load categories.json — default colours apply", ex);
            return [];
        }
    }

    internal static CategoryPalette LoadPaletteSafe() => new(LoadCategoriesSafe());

    private static IReadOnlyList<ClassificationRule> LoadRules()
    {
        try
        {
            return RulesFile.Load(TallyPaths.RulesPath);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load rules.json — all blocks will be Unclassified", ex);
            return [];
        }
    }
}
