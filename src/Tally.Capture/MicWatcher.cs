using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Tally.Core.Models;

namespace Tally.Capture;

/// <summary>
/// Polls Core Audio capture sessions and emits MicStart/MicEnd per process holding an active
/// microphone stream. Brief gaps are merged downstream by the sessionizer, not here.
/// Runs on the thread pool (WASAPI works from MTA threads).
/// </summary>
public sealed class MicWatcher : IDisposable
{
    public event Action<TrackedEvent>? EventCaptured;

    private readonly System.Threading.Timer _timer;
    private HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private int _polling;

    public MicWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            var nowActive = ReadActiveCaptureProcesses();
            if (nowActive is null)
                return;   // audio stack hiccup — keep previous state rather than emitting false MicEnds

            var timestamp = DateTimeOffset.Now;
            foreach (var started in nowActive.Except(_activeProcesses, StringComparer.OrdinalIgnoreCase))
            {
                EventCaptured?.Invoke(new TrackedEvent
                {
                    Timestamp = timestamp,
                    Kind = EventKind.MicStart,
                    ProcessName = started,
                });
            }

            foreach (var stopped in _activeProcesses.Except(nowActive, StringComparer.OrdinalIgnoreCase))
            {
                EventCaptured?.Invoke(new TrackedEvent
                {
                    Timestamp = timestamp,
                    Kind = EventKind.MicEnd,
                    ProcessName = stopped,
                });
            }

            _activeProcesses = nowActive;
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private static HashSet<string>? ReadActiveCaptureProcesses()
    {
        try
        {
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        if (sessions is null)
                            continue;

                        for (var i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session.State != AudioSessionState.AudioSessionStateActive)
                                continue;

                            var pid = session.GetProcessID;
                            if (pid == 0 || pid == (uint)Environment.ProcessId)
                                continue;

                            var name = WindowInfo.ProcessNameFromPid(pid);
                            if (name != WindowInfo.UnknownProcess)
                                active.Add(name);
                        }
                    }
                    catch (Exception)
                    {
                        // One device failing shouldn't kill the whole poll.
                    }
                }
            }

            return active;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _timer.Dispose();
}
