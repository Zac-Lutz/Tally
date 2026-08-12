using Microsoft.EntityFrameworkCore;
using Tally.Core;
using Tally.Core.Models;

namespace Tally.App;

/// <summary>A day's classified sessions, ready to render into any report format or the live view.</summary>
public sealed record ReportData(
    DateOnly Date,
    IReadOnlyList<ClassifiedBlock> Blocks,
    IReadOnlyList<CallSpan> Calls,
    IReadOnlyList<InactivePeriod> Inactive);

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
        List<ActivitySample> samples;
        await using (var db = new TallyDbContext(dbOptions))
        {
            TallyDbContext.EnsureSchema(db);
            events = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd)
                .OrderBy(e => e.Timestamp)
                .ToListAsync();
            samples = await db.ActivitySamples.AsNoTracking()
                .Where(s => s.Timestamp >= dayStart && s.Timestamp < dayEnd)
                .ToListAsync();
        }

        var now = DateTimeOffset.Now;
        var endOfData = now < dayEnd
            ? now
            : events.Count > 0 ? events[^1].Timestamp : dayEnd;

        var sessions = Sessionizer.Build(events, endOfData);
        var classifier = new Classifier(LoadRules());
        var classified = sessions.Blocks
            .Select(b => new ClassifiedBlock(
                b, classifier.Classify(b.ProcessName, b.Title), ActivityAttribution.For(b, samples)))
            .ToList();

        return new ReportData(date, classified, sessions.Calls, sessions.InactivePeriods);
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
        var jsonContext = new JsonExportContext("tally", Environment.MachineName, DateTimeOffset.Now);
        var content = format switch
        {
            ReportFileFormat.Markdown =>
                ReportWriter.BuildMarkdown(data.Date, data.Blocks, data.Calls, data.Inactive),
            ReportFileFormat.Json =>
                JsonExportWriter.BuildJson(data.Date, data.Blocks, data.Calls, jsonContext),
            _ => HtmlReportWriter.BuildHtml(data.Date, data.Blocks, data.Calls, data.Inactive,
                embeddedJson: JsonExportWriter.BuildJson(data.Date, data.Blocks, data.Calls, jsonContext)),
        };

        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(reportsDirectory, $"{date:yyyy-MM-dd}_{DateTime.Now:HHmmss}.{format.Extension()}");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

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
