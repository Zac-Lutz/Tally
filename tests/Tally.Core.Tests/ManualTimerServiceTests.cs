using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class ManualTimerServiceTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
    }

    private static (ManualTimerService svc, List<ManualTimer> saved, Clock clock) NewService()
    {
        var saved = new List<ManualTimer>();
        var clock = new Clock();
        var svc = new ManualTimerService(saved.Add, () => clock.Now);
        return (svc, saved, clock);
    }

    [Fact]
    public void Start_ThenStop_PersistsOneTimer_WithNameAndDuration()
    {
        var (svc, saved, clock) = NewService();
        svc.PendingName = "Ticket #123";
        svc.Start();

        Assert.True(svc.IsActive);
        clock.Now = clock.Now.AddMinutes(25);
        Assert.Equal(TimeSpan.FromMinutes(25), svc.Elapsed);

        svc.Stop();

        Assert.False(svc.IsActive);
        var t = Assert.Single(saved);
        Assert.Equal("Ticket #123", t.Name);
        Assert.Equal(TimeSpan.FromMinutes(25), t.Duration);
    }

    [Fact]
    public void Start_UsesArgName_OverPending()
    {
        var (svc, _, _) = NewService();
        svc.PendingName = "old";
        svc.Start("new name");

        Assert.Equal("new name", svc.Active!.Name);
        Assert.Equal("new name", svc.PendingName);
    }

    [Fact]
    public void EmptyName_BecomesDefault()
    {
        var (svc, _, _) = NewService();
        svc.Start("   ");

        Assert.Equal(ManualTimerService.DefaultName, svc.Active!.Name);
    }

    [Fact]
    public void StartingWhileActive_PersistsThePrevious_AndStartsNew()
    {
        var (svc, saved, clock) = NewService();
        svc.Start("first");
        clock.Now = clock.Now.AddMinutes(10);
        svc.Start("second");

        var t = Assert.Single(saved);
        Assert.Equal("first", t.Name);
        Assert.Equal(TimeSpan.FromMinutes(10), t.Duration);
        Assert.Equal("second", svc.Active!.Name);
    }

    [Fact]
    public void Rename_UpdatesActiveAndPending_LiveTimerKeepsStart()
    {
        var (svc, saved, clock) = NewService();
        svc.Start("before");
        var startedAt = svc.Active!.StartedAt;
        clock.Now = clock.Now.AddMinutes(3);
        svc.Rename("after");

        Assert.Equal("after", svc.Active!.Name);
        Assert.Equal(startedAt, svc.Active!.StartedAt);   // rename doesn't restart
        Assert.Equal("after", svc.PendingName);

        clock.Now = clock.Now.AddMinutes(2);
        svc.Stop();
        Assert.Equal("after", Assert.Single(saved).Name);
    }

    [Fact]
    public void Stop_WhenNotRunning_DoesNothing()
    {
        var (svc, saved, _) = NewService();
        svc.Stop();
        Assert.Empty(saved);
        Assert.False(svc.IsActive);
    }

    [Fact]
    public void ZeroDurationTimer_IsNotPersisted()
    {
        var (svc, saved, _) = NewService();   // clock never advances
        svc.Start("instant");
        svc.Stop();
        Assert.Empty(saved);
    }

    [Fact]
    public void Toggle_StartsThenStops()
    {
        var (svc, saved, clock) = NewService();
        svc.Toggle("work");
        Assert.True(svc.IsActive);
        clock.Now = clock.Now.AddMinutes(1);
        svc.Toggle();
        Assert.False(svc.IsActive);
        Assert.Single(saved);
    }

    [Fact]
    public void Changed_FiresOnEachTransition()
    {
        var (svc, _, _) = NewService();
        var count = 0;
        svc.Changed += () => count++;
        svc.Start("a");   // 1
        svc.Rename("b");  // 2
        svc.Stop();       // 3
        Assert.Equal(3, count);
    }
}
