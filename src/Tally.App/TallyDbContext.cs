using Microsoft.EntityFrameworkCore;
using Tally.Core.Models;

namespace Tally.App;

public sealed class TallyDbContext(DbContextOptions<TallyDbContext> options) : DbContext(options)
{
    public DbSet<TrackedEvent> Events => Set<TrackedEvent>();
    public DbSet<ActivitySample> ActivitySamples => Set<ActivitySample>();

    public static DbContextOptions<TallyDbContext> BuildOptions(string databasePath)
        => new DbContextOptionsBuilder<TallyDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

    /// <summary>
    /// Creates the schema for a fresh database, and additively creates the activity_samples
    /// table on an older database that predates it. There are no EF migrations (this is a
    /// personal single-writer app), so this idempotent step stands in for one; call it before
    /// any read or write.
    /// </summary>
    public static void EnsureSchema(TallyDbContext db)
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS activity_samples (
                Id INTEGER NOT NULL CONSTRAINT PK_activity_samples PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER NOT NULL,
                Keystrokes INTEGER NOT NULL,
                MouseClicks INTEGER NOT NULL);
            """);
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_activity_samples_Timestamp ON activity_samples (Timestamp);");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var events = modelBuilder.Entity<TrackedEvent>();
        events.ToTable("events");
        events.HasKey(e => e.Id);

        // Stored as UTC ticks so range queries compare correctly regardless of offset;
        // values read back carry a UTC offset and are converted to local time for display.
        events.Property(e => e.Timestamp)
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        events.HasIndex(e => e.Timestamp);

        events.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
        events.Property(e => e.ProcessName).HasMaxLength(260);

        var samples = modelBuilder.Entity<ActivitySample>();
        samples.ToTable("activity_samples");
        samples.HasKey(s => s.Id);
        samples.Property(s => s.Timestamp)
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        samples.HasIndex(s => s.Timestamp);
    }
}
