using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Periodic timer that pushes <see cref="ReconciliationRequested"/> into the
/// <see cref="DebouncingEventQueue"/>. The actual reconciliation work happens
/// in the <see cref="IncrementalIndexer"/> worker, not in the timer callback.
/// </summary>
/// <remarks>
/// <para>
/// Default interval: 5 minutes. This is the safety net that handles:
/// </para>
/// <list type="bullet">
///   <item><description>Dropped FSW events (inherently unreliable on all platforms).</description></item>
///   <item><description>External git operations the HeadPoller missed.</description></item>
///   <item><description>Files modified while the daemon was stopped.</description></item>
/// </list>
/// </remarks>
public sealed class ReconciliationScheduler : IDisposable
{
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private Timer? _timer;

    /// <summary>
    /// Create a reconciliation scheduler.
    /// </summary>
    /// <param name="queue">Target queue for <see cref="ReconciliationRequested"/> events.</param>
    /// <param name="interval">Reconciliation interval (default 5 minutes).</param>
    /// <param name="logger">Optional logger.</param>
    public ReconciliationScheduler(
        DebouncingEventQueue queue,
        TimeSpan? interval = null,
        ILogger? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Start the timer. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(OnTick, null, _interval, _interval);
        _logger.LogDebug("ReconciliationScheduler started, interval={Interval}min", _interval.TotalMinutes);
    }

    /// <summary>Stop and dispose the timer.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick(object? state)
    {
        try
        {
            _logger.LogDebug("ReconciliationScheduler tick — enqueuing reconciliation");
            _queue.Enqueue(new ReconciliationRequested());
        }
        catch (Exception ex)
        {
            // Guard against InvalidOperationException if the channel was
            // completed before the scheduler was disposed (shutdown race).
            _logger.LogDebug(ex, "ReconciliationScheduler tick failed (shutdown race?)");
        }
    }
}
