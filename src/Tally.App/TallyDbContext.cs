using Microsoft.EntityFrameworkCore;
using Tally.Core.Models;

namespace Tally.App;

public sealed class TallyDbContext(DbContextOptions<TallyDbContext> options) : DbContext(options)
{
    public DbSet<TrackedEvent> Events => Set<TrackedEvent>();

    public static DbContextOptions<TallyDbContext> BuildOptions(string databasePath)
        => new DbContextOptionsBuilder<TallyDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

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
    }
}
