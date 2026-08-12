using System.Threading.Channels;
using Tally.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Tally.App;

/// <summary>Buffers captured events off the capture threads and writes them to SQLite in small batches.</summary>
public sealed class EventRecorder : IAsyncDisposable
{
    private readonly DbContextOptions<TallyDbContext> _dbOptions;
    private readonly Channel<TrackedEvent> _channel = Channel.CreateUnbounded<TrackedEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writerTask;

    public EventRecorder(DbContextOptions<TallyDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public bool Paused { get; set; }

    public void Record(TrackedEvent trackedEvent)
    {
        if (!Paused)
            _channel.Writer.TryWrite(trackedEvent);
    }

    private async Task WriteLoopAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<TrackedEvent>();
            while (batch.Count < 200 && reader.TryRead(out var item))
                batch.Add(item);

            try
            {
                await using var db = new TallyDbContext(_dbOptions);
                db.Events.AddRange(batch);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to persist {batch.Count} event(s)", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }
}
