using System.Threading.Channels;
using Tally.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Tally.App;

/// <summary>Buffers captured events off the capture threads and writes them to SQLite in small batches.</summary>
public sealed class EventRecorder : IAsyncDisposable
{
    private readonly record struct WriteItem(TrackedEvent? Event, ManualTimer? Timer);

    private readonly DbContextOptions<TallyDbContext> _dbOptions;
    private readonly Channel<WriteItem> _channel = Channel.CreateUnbounded<WriteItem>(
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
            _channel.Writer.TryWrite(new WriteItem(trackedEvent, null));
    }

    // Manual timers are user-declared, so they persist even while auto-tracking is paused.
    public void RecordTimer(ManualTimer timer)
        => _channel.Writer.TryWrite(new WriteItem(null, timer));

    private async Task WriteLoopAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var events = new List<TrackedEvent>();
            var timers = new List<ManualTimer>();
            while (events.Count + timers.Count < 200 && reader.TryRead(out var item))
            {
                if (item.Event is { } e)
                    events.Add(e);
                else if (item.Timer is { } t)
                    timers.Add(t);
            }

            if (events.Count == 0 && timers.Count == 0)
                continue;

            try
            {
                await using var db = new TallyDbContext(_dbOptions);
                if (events.Count > 0)
                    db.Events.AddRange(events);
                if (timers.Count > 0)
                    db.ManualTimers.AddRange(timers);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to persist {events.Count} event(s) + {timers.Count} timer(s)", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }
}
