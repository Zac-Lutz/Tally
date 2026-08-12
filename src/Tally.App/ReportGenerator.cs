using Microsoft.EntityFrameworkCore;
using Tally.Core;
using Tally.Core.Models;

namespace Tally.App;

public static class ReportGenerator
{
    /// <summary>
    /// Builds the markdown report for a local calendar day and writes it to
    /// <paramref name="reportsDirectory"/>. Each run gets its own timestamped file
    /// (yyyy-MM-dd_HHmmss.md, report date + run time), so successive runs never overwrite.
    /// </summary>
    public static async Task<string> GenerateAsync(
        DbContextOptions<TallyDbContext> dbOptions, DateOnly date, string reportsDirectory)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));   // local midnight
        var dayEnd = dayStart.AddDays(1);

        List<TrackedEvent> events;
        await using (var db = new TallyDbContext(dbOptions))
        {
            events = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd)
                .OrderBy(e => e.Timestamp)
                .ToListAsync();
        }

        // For today, open blocks/calls run to "now"; for past days, clamp to the last thing seen.
        var now = DateTimeOffset.Now;
        var endOfData = now < dayEnd
            ? now
            : events.Count > 0 ? events[^1].Timestamp : dayEnd;

        var sessions = Sessionizer.Build(events, endOfData);
        var classifier = new Classifier(LoadRules());
        var classified = sessions.Blocks
            .Select(b => new ClassifiedBlock(b, classifier.Classify(b.ProcessName, b.Title)))
            .ToList();

        var markdown = ReportWriter.BuildMarkdown(date, classified, sessions.Calls, sessions.InactivePeriods);
        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(reportsDirectory, $"{date:yyyy-MM-dd}_{DateTime.Now:HHmmss}.md");
        await File.WriteAllTextAsync(path, markdown);
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
