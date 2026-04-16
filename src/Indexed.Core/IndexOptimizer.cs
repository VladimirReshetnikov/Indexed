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
    private long _lastMergeTicks;          // DateTimeOffset.UtcNow.UtcTicks of last successful merge; 0 = never
    private long _lastMergeElapsedMs;      // Wall-clock ms of the last successful merge; 0 = never

    /// <summary>
    /// 0 = live, 1 = disposed. Flipped atomically by <see cref="DisposeAsync"/>
    /// to keep concurrent dispose callers safe (the daemon shutdown path is
    /// single-threaded today, but the invariant is enforced here rather than
    /// delegated to the caller).
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Total successful merges since construction. Incremented once per
    /// cycle that opens a writer scope and completes without throwing.
    /// </summary>
    public int MergeCount => Volatile.Read(ref _mergeCount);

    /// <summary>
    /// UTC timestamp of the most recent successful merge, or <c>null</c> when
    /// no merge has completed yet. Exposed so <c>/status</c> can surface
    /// optimizer liveness without opening a reader scope.
    /// </summary>
    public DateTimeOffset? LastMergeAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMergeTicks);
            if (ticks == 0) return null;
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Wall-clock milliseconds spent inside the most recent successful merge,
    /// or <c>null</c> when no merge has completed yet.
    /// </summary>
    public long? LastMergeElapsedMs
    {
        get
        {
            var ms = Interlocked.Read(ref _lastMergeElapsedMs);
            return Volatile.Read(ref _mergeCount) == 0 ? null : ms;
        }
    }

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
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(IndexOptimizer));
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
        if (Volatile.Read(ref _disposed) != 0) return;
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
        if (Volatile.Read(ref _disposed) != 0) return false;

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
                RecordMergeCompletion(sw.ElapsedMilliseconds);
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
    /// Atomically stamp the observability counters after a successful merge.
    /// </summary>
    private void RecordMergeCompletion(long elapsedMs)
    {
        Interlocked.Increment(ref _mergeCount);
        Interlocked.Exchange(ref _lastMergeTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Exchange(ref _lastMergeElapsedMs, elapsedMs);
    }

    /// <summary>
    /// Default shutdown deadline for <see cref="DisposeAsync"/>. Chosen so
    /// the typical final merge completes well inside the window (tens of ms
    /// on SSDs with the default 1024-page budget) but a pathological
    /// contention case cannot wedge daemon shutdown for more than ~10 s.
    /// </summary>
    public static TimeSpan DefaultShutdownTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stop the timer and — if a merge is still pending — run one final
    /// merge at double the normal page budget. Exceptions from the final
    /// merge are swallowed (best-effort compaction on shutdown).
    /// </summary>
    /// <remarks>
    /// The final merge runs under a bounded deadline so daemon shutdown
    /// cannot be blocked indefinitely by writer-lock contention. If the
    /// deadline elapses while waiting for the cycle gate or during the
    /// merge itself, the call returns quietly and the next daemon start
    /// picks up the compaction work.
    /// </remarks>
    public ValueTask DisposeAsync() => DisposeAsync(DefaultShutdownTimeout);

    /// <summary>
    /// Variant of <see cref="DisposeAsync()"/> that accepts an explicit
    /// shutdown deadline. Primarily for tests and callers with bespoke
    /// shutdown budgets; production code should use the parameterless form.
    /// </summary>
    public async ValueTask DisposeAsync(TimeSpan shutdownTimeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stop the timer first so no more ticks fire concurrently with the
        // final merge. Timer.DisposeAsync waits for an in-flight callback.
        var timer = _timer;
        _timer = null;
        if (timer is not null)
        {
            try { await timer.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        // Best-effort final merge at 2x the per-tick budget. Runs only if
        // dirty — a clean optimizer has nothing to compact. Bounded by
        // shutdownTimeout so daemon shutdown cannot hang on writer-lock
        // contention or a slow FTS5 structure rewrite.
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) == 1)
        {
            using var deadlineCts = new CancellationTokenSource(shutdownTimeout);
            var entered = false;
            try
            {
                try
                {
                    await _cycleGate.WaitAsync(deadlineCts.Token).ConfigureAwait(false);
                    entered = true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug(
                        "final fts merge skipped — shutdown deadline elapsed while waiting for cycle gate");
                }

                if (entered)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await _index.RunFts5MergeAsync(_pageBudget * 2, deadlineCts.Token)
                            .ConfigureAwait(false);
                        RecordMergeCompletion(sw.ElapsedMilliseconds);
                        _logger.LogInformation(
                            "final fts merge complete pages={Pages} elapsed={ElapsedMs}ms",
                            _pageBudget * 2, sw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug(
                            "final fts merge aborted — shutdown deadline elapsed mid-merge");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "final fts merge on shutdown failed (best-effort)");
                    }
                }
            }
            finally
            {
                if (entered) _cycleGate.Release();
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
