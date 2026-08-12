using System.Threading.Channels;
using Tally.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Tally.App;

/// <summary>Buffers captured events off the capture threads and writes them to SQLite in small batches.</summary>
public sealed class EventRecorder : IAsyncDisposable
{
    private readonly record struct WriteItem(TrackedEvent? Event, ActivitySample? Sample);

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

    public void RecordSample(ActivitySample sample)
    {
        if (!Paused)
            _channel.Writer.TryWrite(new WriteItem(null, sample));
    }

    private async Task WriteLoopAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var events = new List<TrackedEvent>();
            var samples = new List<ActivitySample>();
            while (events.Count + samples.Count < 200 && reader.TryRead(out var item))
            {
                if (item.Event is { } e)
                    events.Add(e);
                else if (item.Sample is { } s)
                    samples.Add(s);
            }

            if (events.Count == 0 && samples.Count == 0)
                continue;

            try
            {
                await using var db = new TallyDbContext(_dbOptions);
                if (events.Count > 0)
                    db.Events.AddRange(events);
                if (samples.Count > 0)
                    db.ActivitySamples.AddRange(samples);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to persist {events.Count} event(s) + {samples.Count} sample(s)", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }
}
