using System;
using System.Threading;
using Indexed.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Polls <c>.git/index</c> mtime and <c>git rev-parse HEAD</c> on a timer,
/// pushing <see cref="HeadMoved"/> into the <see cref="DebouncingEventQueue"/>
/// when HEAD has changed since the last known <c>indexed_head</c>.
/// </summary>
/// <remarks>
/// <para>
/// The mtime check makes the common case (no git operation in the last tick)
/// a single <c>stat()</c> call — no process spawn. Only when the mtime
/// changes does the poller run <c>git rev-parse HEAD</c>.
/// </para>
/// <para>
/// Default interval: 1 second. This is cheap enough to run indefinitely;
/// the mtime optimization ensures zero git subprocesses when the developer
/// isn't actively committing/switching.
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
    private DateTimeOffset? _lastIndexMtime;
    private string? _lastKnownHead;
    private int _consecutiveErrors;

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
        _lastKnownHead = _index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
        _lastIndexMtime = _repo.GetIndexMtime();

        _timer = new Timer(OnTick, null, _interval, _interval);
        _logger.LogDebug("HeadPoller started, interval={Interval}ms", _interval.TotalMilliseconds);
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
            // Cheap mtime check first.
            var currentMtime = _repo.GetIndexMtime();
            if (_lastIndexMtime.HasValue
                && currentMtime.HasValue
                && currentMtime.Value == _lastIndexMtime.Value)
            {
                // No git operation has touched .git/index since last tick.
                return;
            }
            _lastIndexMtime = currentMtime;

            // Mtime changed — check the actual HEAD.
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

            var indexedHead = _lastKnownHead;
            if (string.Equals(currentHead, indexedHead, StringComparison.Ordinal))
                return;

            // HEAD has moved.
            _logger.LogInformation("HEAD moved: {Old} -> {New}",
                indexedHead?[..Math.Min(7, indexedHead.Length)] ?? "(null)",
                currentHead[..Math.Min(7, currentHead.Length)]);

            _queue.Enqueue(new HeadMoved(indexedHead ?? string.Empty, currentHead));
            _lastKnownHead = currentHead;

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
