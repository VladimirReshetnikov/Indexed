using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Low-priority background task that runs bounded FTS5 segment merges on an
/// interval. Fragmented segments inflate <c>code_fts_data</c> by 10–25%
/// vs. a merged steady state; this component drives SQLite's incremental
/// merge ("merge" command) into the writer at idle times.
/// </summary>
/// <remarks>
/// <para>
/// Behavior. A periodic <see cref="Timer"/> fires every
/// <see cref="TimeSpan">interval</see> (default 15 min). If
/// <see cref="NotifyDirty"/> has been called since the previous tick, the
/// optimizer acquires a writer scope via <see cref="SqliteIndex.RunFts5MergeAsync"/>
/// with the configured page budget (default 512). Clean ticks (no intervening
/// batch commit) are skipped outright — no writer scope is opened.
/// </para>
/// <para>
/// Serialization vs. the incremental indexer.
/// <see cref="SqliteIndex.BeginWriteAsync"/> already serializes writers.
/// The per-tick page budget bounds wall-clock occupation, so the optimizer
/// cannot starve the indexer under write pressure. If the optimizer fails
/// to merge (exception inside <c>RunFts5MergeAsync</c>), the dirty flag is
/// restored so the next tick will retry.
/// </para>
/// <para>
/// Shutdown. <see cref="DisposeAsync"/> stops the timer, and — if the dirty
/// flag is still set — runs one final merge at twice the normal page budget,
/// so the DB is closer to a compact steady state on the next start. The final
/// merge is skipped silently if not dirty or if it fails.
/// </para>
/// <para>
/// Observability. Tests use <see cref="MergeCount"/> and
/// <see cref="RunCycleAsync"/> to drive merges deterministically without
/// sleeping for the timer period.
/// </para>
/// </remarks>
public sealed class IndexOptimizer : IAsyncDisposable
{
    private readonly SqliteIndex _index;
    private readonly TimeSpan _interval;
    private readonly int _pageBudget;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _cycleGate = new(initialCount: 1, maxCount: 1);

    private Timer? _timer;

    /// <summary>
    /// 0 = clean, 1 = dirty. Flipped to 1 by <see cref="NotifyDirty"/> and
    /// cleared by the cycle via <see cref="Interlocked.Exchange(ref int, int)"/>.
    /// Using an int for interop with <c>Interlocked</c>.
    /// </summary>
    private int _dirty;

    private int _mergeCount;
    private volatile bool _disposed;

    /// <summary>
    /// Total successful merges since construction. Incremented once per
    /// cycle that opens a writer scope and completes without throwing.
    /// </summary>
    public int MergeCount => Volatile.Read(ref _mergeCount);

    /// <summary>
    /// Create an optimizer bound to <paramref name="index"/>. The timer is not
    /// started until <see cref="Start"/> is called.
    /// </summary>
    /// <param name="index">Index whose FTS5 tables should be periodically merged.</param>
    /// <param name="interval">Tick interval; <c>null</c> = 15 minutes.</param>
    /// <param name="pageBudget">FTS5 pages per merge call; <c>null</c> = 512.</param>
    /// <param name="logger">Optional logger (defaults to a null logger).</param>
    public IndexOptimizer(
        SqliteIndex index,
        TimeSpan? interval = null,
        int? pageBudget = null,
        ILogger? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _interval = interval ?? TimeSpan.FromMinutes(15);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "interval must be positive");
        _pageBudget = pageBudget ?? 512;
        if (_pageBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageBudget), "pageBudget must be positive");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Start the background timer. Idempotent.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IndexOptimizer));
        if (_timer is not null) return;
        _timer = new Timer(OnTick, state: null, dueTime: _interval, period: _interval);
        _logger.LogDebug(
            "IndexOptimizer started, interval={IntervalMin}min pageBudget={PageBudget}",
            _interval.TotalMinutes, _pageBudget);
    }

    /// <summary>
    /// Mark the index as having received writes since the last merge. Cheap
    /// to call from hot paths (wired to <c>IncrementalIndexer.BatchCommitted</c>).
    /// Safe to call concurrently with the tick.
    /// </summary>
    public void NotifyDirty()
    {
        // No-op after dispose — the optimizer may be torn down before the
        // indexer's last event fires.
        if (_disposed) return;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Execute one merge cycle synchronously. Primarily for tests — production
    /// code relies on the timer. Returns <c>true</c> when a merge ran,
    /// <c>false</c> when the cycle was skipped due to a clean dirty flag.
    /// </summary>
    /// <param name="pageBudgetOverride">
    /// Optional per-call override (used by the final shutdown merge). Must be
    /// positive. <c>null</c> = use the configured <c>pageBudget</c>.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async ValueTask<bool> RunCycleAsync(
        int? pageBudgetOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        var budget = pageBudgetOverride ?? _pageBudget;
        if (budget <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageBudgetOverride), "pageBudget must be positive");

        // Atomically consume the dirty flag. If it was already 0, nothing to do.
        var wasDirty = Interlocked.Exchange(ref _dirty, 0);
        if (wasDirty == 0)
        {
            _logger.LogDebug("IndexOptimizer tick skipped — nothing dirty");
            return false;
        }

        // Serialize concurrent RunCycleAsync invocations with each other so a
        // test-driven cycle cannot race with a timer-driven cycle.
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _index.RunFts5MergeAsync(budget, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _mergeCount);
                _logger.LogInformation(
                    "merged fts pages={Pages} elapsed={ElapsedMs}ms",
                    budget, sw.ElapsedMilliseconds);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Cancellation during the writer-lock wait is not a failure
                // worth reporting; restore the dirty flag so the next tick
                // retries.
                Volatile.Write(ref _dirty, 1);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "IndexOptimizer merge failed after {ElapsedMs}ms — will retry next tick",
                    sw.ElapsedMilliseconds);
                // Restore dirty so the next tick re-attempts the merge.
                Volatile.Write(ref _dirty, 1);
                return false;
            }
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Stop the timer and — if a merge is still pending — run one final
    /// merge at double the normal page budget. Exceptions from the final
    /// merge are swallowed (best-effort compaction on shutdown).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the timer first so no more ticks fire concurrently with the
        // final merge. Timer.DisposeAsync waits for an in-flight callback.
        var timer = _timer;
        _timer = null;
        if (timer is not null)
        {
            try { await timer.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        // Best-effort final merge at 2x the per-tick budget. Runs only if
        // dirty — a clean optimizer has nothing to compact.
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) == 1)
        {
            await _cycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await _index.RunFts5MergeAsync(_pageBudget * 2, CancellationToken.None)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _mergeCount);
                    _logger.LogInformation(
                        "final fts merge complete pages={Pages} elapsed={ElapsedMs}ms",
                        _pageBudget * 2, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "final fts merge on shutdown failed (best-effort)");
                }
            }
            finally
            {
                _cycleGate.Release();
            }
        }

        _cycleGate.Dispose();
    }

    private void OnTick(object? state)
    {
        // The Timer callback is invoked on a ThreadPool thread. We must not
        // block it with a synchronous .Wait on the async cycle; fire and
        // forget is fine here because _cycleGate + the dirty flag keep
        // concurrent ticks coordinated.
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            await RunCycleAsync(pageBudgetOverride: null, cancellationToken: default)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunCycleAsync already logs and restores the dirty flag on
            // failure; anything here is defensive.
            _logger.LogDebug(ex, "IndexOptimizer tick threw past RunCycleAsync");
        }
    }
}
