using System;
using System.Threading;
using Indexed.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Polls <c>git rev-parse HEAD</c> on a timer, pushing <see cref="HeadMoved"/>
/// into the <see cref="DebouncingEventQueue"/> when HEAD has changed since the
/// last known <c>indexed_head</c>.
/// </summary>
/// <remarks>
/// <para>
/// Default interval: 1 second. A full <c>git rev-parse HEAD</c> runs every
/// tick because the obvious mtime canary (<c>.git/index</c>) does <em>not</em>
/// change on <c>git reset --soft</c>, <c>git update-ref</c>, branch fast-forward
/// from a fetch, or any other HEAD mutation that leaves the staging area
/// untouched — historically this caused HEAD moves to be silently dropped.
/// </para>
/// <para>
/// The subprocess cost (~5–20 ms on a warm repo) is acceptable at 1 Hz on
/// developer workstations; the alternative — watching <c>.git/HEAD</c> plus
/// the resolved ref file plus <c>.git/packed-refs</c> — trades correctness
/// risk (ref packing races, worktree indirection) for marginal savings.
/// </para>
/// </remarks>
public sealed class HeadPoller : IDisposable
{
    private readonly GitRepository _repo;
    private readonly SqliteIndex _index;
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private Timer? _timer;
    // _lastKnownHead is written by the timer thread in OnTick and read by
    // HTTP request threads via LastKnownHead. All accesses go through
    // Volatile.Read/Volatile.Write to guarantee the cross-thread reader sees
    // either null or a fully-published reference (x86/x64 already do this in
    // practice, but the invariant is explicit in the code so that weaker
    // memory-model architectures do not silently regress).
    private string? _lastKnownHead;
    private int _consecutiveErrors;

    /// <summary>
    /// Last known HEAD SHA, updated on each successful poll tick. Read by
    /// <c>BuildFreshness()</c> to avoid spawning <c>git rev-parse HEAD</c>
    /// per HTTP request.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Volatile.Read{T}(ref readonly T)"/> to pair with the
    /// <see cref="Volatile.Write{T}(ref T, T)"/> inside the polling callback;
    /// the returned reference is either <see langword="null"/> or a fully
    /// published SHA string, never a torn write.
    /// </remarks>
    public string? LastKnownHead => Volatile.Read(ref _lastKnownHead);

    /// <summary>
    /// Create a poller.
    /// </summary>
    /// <param name="repo">Repository to poll.</param>
    /// <param name="index">Index instance for reading <c>indexed_head</c> meta.</param>
    /// <param name="queue">Target queue for <see cref="HeadMoved"/> events.</param>
    /// <param name="interval">Polling interval (default 1 second).</param>
    /// <param name="logger">Optional logger.</param>
    public HeadPoller(
        GitRepository repo,
        SqliteIndex index,
        DebouncingEventQueue queue,
        TimeSpan? interval = null,
        ILogger? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Start polling. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null) return;

        // Seed the last-known HEAD from the index meta so we detect drift
        // immediately on the first tick.
        Volatile.Write(ref _lastKnownHead, _index.GetMeta(SqliteSchema.MetaKey_IndexedHead));

        _timer = new Timer(OnTick, null, _interval, _interval);
        _logger.LogDebug("HeadPoller started, interval={Interval}ms", _interval.TotalMilliseconds);
    }

    /// <summary>Stop and dispose the timer.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Run the polling logic once on the calling thread. Intended for
    /// deterministic testing — production code uses <see cref="Start"/>,
    /// which schedules the same callback on a <see cref="Timer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seeds <c>_lastKnownHead</c> from persistent <c>indexed_head</c> meta
    /// on the first call (matching <see cref="Start"/>'s behavior) so tests
    /// don't have to reach inside the type to set up the baseline.
    /// </para>
    /// </remarks>
    internal void PollOnce()
    {
        if (Volatile.Read(ref _lastKnownHead) is null)
            Volatile.Write(ref _lastKnownHead, _index.GetMeta(SqliteSchema.MetaKey_IndexedHead));
        OnTick(state: null);
    }

    private void OnTick(object? state)
    {
        try
        {
            // Read the actual HEAD every tick. The previous implementation
            // short-circuited on unchanged .git/index mtime, but that canary
            // misses HEAD-only mutations (reset --soft, update-ref, fetch
            // fast-forward) — the index file is not touched in those cases,
            // so the poller never spotted them. A plain rev-parse at 1 Hz
            // is cheap enough and restores correctness.
            string currentHead;
            try
            {
                currentHead = _repo.GetHeadSha();
            }
            catch (GitProcessException)
            {
                // Transient error (unborn HEAD during rebase, etc.) — skip.
                return;
            }

            if (string.IsNullOrEmpty(currentHead)) return;

            var indexedHead = Volatile.Read(ref _lastKnownHead);
            if (string.Equals(currentHead, indexedHead, StringComparison.Ordinal))
                return;

            // HEAD has moved.
            _logger.LogInformation("HEAD moved: {Old} -> {New}",
                indexedHead?[..Math.Min(7, indexedHead.Length)] ?? "(null)",
                currentHead[..Math.Min(7, currentHead.Length)]);

            _queue.Enqueue(new HeadMoved(indexedHead ?? string.Empty, currentHead));
            Volatile.Write(ref _lastKnownHead, currentHead);

            // Reset error counter on success.
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            // Logarithmic backoff: log at Warning only every 2^N errors to
            // avoid 1-per-second log spam when the error is persistent.
            if (_consecutiveErrors <= 1 || (_consecutiveErrors & (_consecutiveErrors - 1)) == 0)
            {
                _logger.LogWarning(ex, "HeadPoller tick error (consecutive: {Count})", _consecutiveErrors);
            }
        }
    }
}
