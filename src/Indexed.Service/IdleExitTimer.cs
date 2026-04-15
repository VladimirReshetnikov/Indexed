using System;
using System.Threading;

namespace Indexed.Service;

/// <summary>
/// Tracks the time of the most recent daemon activity and invokes a callback
/// once the configured idle window elapses with no further signals.
/// </summary>
/// <remarks>
/// <para>
/// Any activity that should keep the daemon alive — a handled HTTP request,
/// index work, a watcher event — calls <see cref="Poke"/>. The timer resets
/// and restarts the countdown on every poke. When the window elapses without
/// a poke the callback fires exactly once; subsequent pokes have no effect.
/// </para>
/// <para>
/// Sleep resilience: after the machine resumes from sleep, <see cref="System.Threading.Timer"/>
/// fires immediately for missed deadlines. A naïve timer would kill the daemon
/// the instant the lid opens. This implementation records the last-poke
/// timestamp and re-checks elapsed time in <see cref="OnTimer"/> — if the
/// actual idle time is less than the window (because the machine was asleep),
/// the timer is rescheduled for the remaining duration.
/// </para>
/// <para>
/// Thread-safety: <see cref="Poke"/> is safe to call from any thread. The
/// callback runs on a <see cref="ThreadPool"/> thread.
/// </para>
/// </remarks>
internal sealed class IdleExitTimer : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action _onIdle;
    private readonly Timer _timer;
    private long _lastPokeTicks; // Stopwatch-based ticks for monotonic elapsed time
    private int _fired;

    public IdleExitTimer(TimeSpan window, Action onIdle)
    {
        _window = window;
        _onIdle = onIdle;
        _lastPokeTicks = Environment.TickCount64;
        _timer = new Timer(OnTimer, state: null, _window, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Reset the idle countdown. No-op after the callback has fired.
    /// </summary>
    public void Poke()
    {
        if (Volatile.Read(ref _fired) != 0) return;
        Interlocked.Exchange(ref _lastPokeTicks, Environment.TickCount64);
        try
        {
            _timer.Change(_window, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Timer already disposed — ignore.
        }
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _fired) != 0) return;

        // Check actual monotonic elapsed time to handle sleep/resume:
        // if the machine slept for 2 hours, TickCount64 advanced but
        // the daemon wasn't actually idle for that duration.
        var lastPoke = Interlocked.Read(ref _lastPokeTicks);
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastPoke);

        if (elapsed < _window)
        {
            // The timer fired early (e.g., sleep/resume clock jump).
            // Reschedule for the remaining duration.
            var remaining = _window - elapsed;
            try
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
            return;
        }

        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        _onIdle();
    }

    public void Dispose() => _timer.Dispose();
}
