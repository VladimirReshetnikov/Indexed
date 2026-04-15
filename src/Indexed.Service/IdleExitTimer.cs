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
/// Thread-safety: <see cref="Poke"/> is safe to call from any thread. The
/// callback runs on a <see cref="ThreadPool"/> thread.
/// </para>
/// </remarks>
internal sealed class IdleExitTimer : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action _onIdle;
    private readonly Timer _timer;
    private int _fired;

    public IdleExitTimer(TimeSpan window, Action onIdle)
    {
        _window = window;
        _onIdle = onIdle;
        _timer = new Timer(OnTimer, state: null, _window, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Reset the idle countdown. No-op after the callback has fired.
    /// </summary>
    public void Poke()
    {
        if (Volatile.Read(ref _fired) != 0) return;
        _timer.Change(_window, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        _onIdle();
    }

    public void Dispose() => _timer.Dispose();
}
