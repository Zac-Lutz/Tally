using Microsoft.EntityFrameworkCore;
using Tally.Core;

namespace Tally.App;

/// <summary>
/// Daily database upkeep: deletes raw events older than the retention window and compacts the
/// file. Manual timers are never purged — they're user-declared and can't be rebuilt from
/// anything — and old days live on as the report files already written.
/// </summary>
internal static class DatabaseMaintenance
{
    public static async Task PurgeOldEventsAsync(DbContextOptions<TallyDbContext> dbOptions, int retentionDays)
    {
        if (RetentionPolicy.Cutoff(DateOnly.FromDateTime(DateTime.Now), retentionDays) is not { } cutoff)
            return;

        try
        {
            await using var db = new TallyDbContext(dbOptions);
            var deleted = await db.Events
                .Where(e => e.Timestamp < cutoff)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            if (deleted > 0)
            {
                // SQLite keeps freed pages until a VACUUM, so compact only when rows actually went.
                await db.Database.ExecuteSqlRawAsync("VACUUM;").ConfigureAwait(false);
                Log.Info($"Purged {deleted} raw event(s) from before {cutoff:yyyy-MM-dd} and compacted the database");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Raw-event purge failed", ex);
        }
    }
}
