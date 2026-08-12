using Tally.Core.Models;

namespace Tally.Core;

public sealed record SessionizerOptions
{
    /// <summary>Foreground blocks shorter than this are treated as flickers and dropped.</summary>
    public TimeSpan MinBlockDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Adjacent blocks with the same process+title separated by at most this gap are merged.</summary>
    public TimeSpan SameKeyMergeGap { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Mic spans from the same process separated by at most this gap are merged into one call.</summary>
    public TimeSpan CallGapMerge { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record DaySessions(
    IReadOnlyList<Block> Blocks,
    IReadOnlyList<CallSpan> Calls,
    IReadOnlyList<InactivePeriod> InactivePeriods);

/// <summary>Turns a day's raw event stream into foreground blocks, call spans, and inactive periods.</summary>
public static class Sessionizer
{
    public static DaySessions Build(
        IEnumerable<TrackedEvent> events,
        DateTimeOffset endOfData,
        SessionizerOptions? options = null)
    {
        options ??= new SessionizerOptions();

        // IdleStart timestamps are backdated by the capture layer, so the stream is not
        // guaranteed monotonic as emitted — sort before processing.
        var ordered = events.OrderBy(e => e.Timestamp).ToList();

        var calls = BuildCallSpans(ordered, endOfData, options.CallGapMerge);

        var blocks = new List<Block>();
        var inactive = new List<InactivePeriod>();

        string? process = null;
        string? title = null;
        DateTimeOffset? blockStart = null;
        DateTimeOffset? suspendedAt = null;
        string? suspendReason = null;

        void CloseBlock(DateTimeOffset end)
        {
            if (blockStart is { } start && process is not null && end > start)
                blocks.Add(new Block(start, end, process, title ?? string.Empty));
            blockStart = null;
        }

        foreach (var e in ordered)
        {
            switch (e.Kind)
            {
                case EventKind.Focus:
                case EventKind.TitleChange:
                    if (e.ProcessName == process && e.WindowTitle == title)
                        break;
                    if (suspendedAt is null)
                        CloseBlock(e.Timestamp);
                    process = e.ProcessName;
                    title = e.WindowTitle;
                    if (suspendedAt is null)
                        blockStart = e.Timestamp;
                    break;

                case EventKind.IdleStart:
                    // An active call means hands-off time is still working time (sitting back
                    // listening on a meeting), so idle is suppressed while a mic span covers it.
                    if (suspendedAt is not null || IsInCall(calls, e.Timestamp))
                        break;
                    CloseBlock(e.Timestamp);
                    suspendedAt = e.Timestamp;
                    suspendReason = InactiveReasons.Idle;
                    break;

                case EventKind.IdleEnd:
                    if (suspendedAt is { } idleFrom && suspendReason == InactiveReasons.Idle)
                    {
                        inactive.Add(new InactivePeriod(idleFrom, e.Timestamp, InactiveReasons.Idle));
                        suspendedAt = null;
                        suspendReason = null;
                        if (process is not null)
                            blockStart = e.Timestamp;
                    }
                    break;

                case EventKind.Lock:
                    // Lock always suspends the foreground lane. An active call keeps running in
                    // the call lane — still on the meeting, just not at the machine.
                    if (suspendedAt is null)
                    {
                        CloseBlock(e.Timestamp);
                        suspendedAt = e.Timestamp;
                    }
                    suspendReason = InactiveReasons.Locked;
                    break;

                case EventKind.Unlock:
                    if (suspendedAt is { } lockFrom)
                    {
                        inactive.Add(new InactivePeriod(lockFrom, e.Timestamp, suspendReason ?? InactiveReasons.Locked));
                        suspendedAt = null;
                        suspendReason = null;
                        if (process is not null)
                            blockStart = e.Timestamp;
                    }
                    break;
            }
        }

        if (suspendedAt is { } openSuspend)
            inactive.Add(new InactivePeriod(openSuspend, endOfData, suspendReason ?? InactiveReasons.Idle));
        else
            CloseBlock(endOfData);

        return new DaySessions(MergeAndFilter(blocks, options), calls, inactive);
    }

    private static List<Block> MergeAndFilter(List<Block> blocks, SessionizerOptions options)
    {
        var result = new List<Block>();
        foreach (var block in blocks.Where(b => b.Duration >= options.MinBlockDuration))
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.ProcessName == block.ProcessName
                    && last.Title == block.Title
                    && block.Start - last.End <= options.SameKeyMergeGap)
                {
                    result[^1] = last with { End = block.End };
                    continue;
                }
            }
            result.Add(block);
        }

        return result;
    }

    private static List<CallSpan> BuildCallSpans(List<TrackedEvent> ordered, DateTimeOffset endOfData, TimeSpan gapMerge)
    {
        var open = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var raw = new List<CallSpan>();

        foreach (var e in ordered)
        {
            if (e.Kind == EventKind.MicStart)
            {
                open.TryAdd(e.ProcessName, e.Timestamp);
            }
            else if (e.Kind == EventKind.MicEnd
                && open.Remove(e.ProcessName, out var start)
                && e.Timestamp > start)
            {
                raw.Add(new CallSpan(start, e.Timestamp, e.ProcessName, string.Empty));
            }
        }

        foreach (var (openProcess, start) in open)
        {
            if (endOfData > start)
                raw.Add(new CallSpan(start, endOfData, openProcess, string.Empty));
        }

        var merged = new List<CallSpan>();
        foreach (var group in raw.GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            CallSpan? current = null;
            foreach (var span in group.OrderBy(c => c.Start))
            {
                if (current is not null && span.Start - current.End <= gapMerge)
                {
                    current = current with { End = span.End > current.End ? span.End : current.End };
                }
                else
                {
                    if (current is not null)
                        merged.Add(current);
                    current = span;
                }
            }

            if (current is not null)
                merged.Add(current);
        }

        return merged
            .OrderBy(c => c.Start)
            .Select(c => c with { Title = FindCallTitle(ordered, c) ?? string.Empty })
            .ToList();
    }

    private static string? FindCallTitle(List<TrackedEvent> ordered, CallSpan call)
    {
        var withinSpan = ordered.FirstOrDefault(e => IsWindowEventFor(e, call.ProcessName)
            && e.Timestamp >= call.Start && e.Timestamp <= call.End);
        if (withinSpan is not null)
            return withinSpan.WindowTitle;

        // Fall back to the most recent title the process had before the call began —
        // you often focus the Teams window just before the mic goes live.
        return ordered.LastOrDefault(e => IsWindowEventFor(e, call.ProcessName)
            && e.Timestamp < call.Start)?.WindowTitle;
    }

    private static bool IsWindowEventFor(TrackedEvent e, string processName)
        => e.Kind is EventKind.Focus or EventKind.TitleChange
           && e.WindowTitle.Length > 0
           && string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase);

    private static bool IsInCall(List<CallSpan> calls, DateTimeOffset timestamp)
        => calls.Any(c => c.Start <= timestamp && timestamp <= c.End);
}
