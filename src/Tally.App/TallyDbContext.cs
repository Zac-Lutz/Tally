using Microsoft.EntityFrameworkCore;
using Tally.Core.Models;

namespace Tally.App;

public sealed class TallyDbContext(DbContextOptions<TallyDbContext> options) : DbContext(options)
{
    public DbSet<TrackedEvent> Events => Set<TrackedEvent>();
    public DbSet<ManualTimer> ManualTimers => Set<ManualTimer>();

    public static DbContextOptions<TallyDbContext> BuildOptions(string databasePath)
        => new DbContextOptionsBuilder<TallyDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

    /// <summary>
    /// Creates the schema for a fresh database, and additively creates the manual_timers table on
    /// an older database that predates it. There are no EF migrations (this is a personal
    /// single-writer app), so this idempotent step stands in for one; call it before any read or
    /// write. (An old <c>activity_samples</c> table may linger from a prior version — it's unused
    /// and harmless, so it's left in place rather than dropped.)
    /// </summary>
    public static void EnsureSchema(TallyDbContext db)
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS manual_timers (
                Id INTEGER NOT NULL CONSTRAINT PK_manual_timers PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Start INTEGER NOT NULL,
                End INTEGER NOT NULL);
            """);
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_manual_timers_Start ON manual_timers (Start);");
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

        var timers = modelBuilder.Entity<ManualTimer>();
        timers.ToTable("manual_timers");
        timers.HasKey(t => t.Id);
        timers.Property(t => t.Name).HasMaxLength(400);
        timers.Property(t => t.Start)
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        timers.Property(t => t.End)
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        timers.HasIndex(t => t.Start);
    }
}
