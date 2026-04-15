using System;
using System.Threading;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

public sealed class IdleExitTimerTests
{
    [Fact]
    public void FiresOnceAfterWindowElapses()
    {
        var fired = new ManualResetEventSlim();
        using var timer = new IdleExitTimer(TimeSpan.FromMilliseconds(100), () => fired.Set());

        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PokeResetsCountdown()
    {
        var fired = new ManualResetEventSlim();
        using var timer = new IdleExitTimer(TimeSpan.FromMilliseconds(200), () => fired.Set());

        // Poke steadily so the window never elapses.
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(50);
            timer.Poke();
        }

        Assert.False(fired.IsSet);

        // Stop poking; the window should expire.
        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void FiresExactlyOnce()
    {
        var count = 0;
        using var timer = new IdleExitTimer(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref count));

        Thread.Sleep(500);
        timer.Poke();     // has no effect after firing
        Thread.Sleep(200);

        Assert.Equal(1, Volatile.Read(ref count));
    }
}
