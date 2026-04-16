using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Indexed.Core;

/// <summary>
/// Debouncing event queue that collapses rapid-fire file events into batches
/// before forwarding them to the <see cref="IncrementalIndexer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two debounce windows cooperate:
/// </para>
/// <list type="bullet">
///   <item><description><strong>Per-path</strong> (default 250 ms): rapid saves to
///   the same file collapse into one event. Only the last event for a given
///   path within the window survives.</description></item>
///   <item><description><strong>Global batch</strong> (default 500 ms): once any event
///   arrives, the queue waits up to the batch window for more events before
///   emitting the accumulated batch. This keeps transaction overhead low
///   while maintaining responsiveness.</description></item>
/// </list>
/// <para>
/// <see cref="HeadMoved"/> and <see cref="ReconciliationRequested"/> events
/// bypass per-path debouncing and are forwarded immediately (though they
/// still wait for the global batch window).
/// </para>
/// <para>
/// Thread-safety: <see cref="Enqueue"/> is safe to call from any thread
/// (FSW callbacks, timer callbacks, HTTP request threads).
/// <see cref="DequeueAsync"/> is intended for a single consumer.
/// </para>
/// </remarks>
public sealed class DebouncingEventQueue : IDisposable
{
    private readonly TimeSpan _perPathDebounce;
    private readonly TimeSpan _globalBatchWindow;
    private readonly int _maxBatchSize;

    // Unbounded channel for raw incoming events.
    private readonly Channel<IndexEvent> _incoming = Channel.CreateUnbounded<IndexEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    // Pending per-path events with their arrival time.
    private readonly Dictionary<string, (IndexEvent Event, DateTimeOffset Arrival)> _pendingPaths = new(StringComparer.Ordinal);

    // Non-path events (HeadMoved, ReconciliationRequested) that bypass per-path debounce.
    private readonly List<IndexEvent> _pendingGlobal = new();

    /// <summary>
    /// Create a debouncing queue with the specified timing parameters.
    /// </summary>
    /// <param name="perPathDebounce">Time to wait before emitting a per-path event (default 250 ms).</param>
    /// <param name="globalBatchWindow">Time to wait after the first event before emitting a batch (default 500 ms).</param>
    /// <param name="maxBatchSize">Maximum events per batch (default 200).</param>
    public DebouncingEventQueue(
        TimeSpan? perPathDebounce = null,
        TimeSpan? globalBatchWindow = null,
        int maxBatchSize = 200)
    {
        _perPathDebounce = perPathDebounce ?? TimeSpan.FromMilliseconds(250);
        _globalBatchWindow = globalBatchWindow ?? TimeSpan.FromMilliseconds(500);
        _maxBatchSize = maxBatchSize > 0 ? maxBatchSize : 200;
    }

    /// <summary>
    /// Number of distinct pending events (paths + global) awaiting processing.
    /// Read by <c>BuildFreshness()</c> for the <c>PendingFileCount</c> slot.
    /// </summary>
    public int PendingCount => _pendingPaths.Count + _pendingGlobal.Count;

    /// <summary>
    /// Push an event into the queue. Thread-safe; never blocks.
    /// </summary>
    public void Enqueue(IndexEvent evt)
    {
        if (evt is null) return;
        _incoming.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Signal that no more events will be produced. Causes
    /// <see cref="DequeueAsync"/> to drain and return an empty batch.
    /// </summary>
    public void Complete() => _incoming.Writer.TryComplete();

    /// <summary>
    /// Wait for a debounced batch of events. Returns an empty list only
    /// when the queue has been completed and fully drained.
    /// </summary>
    public async ValueTask<IReadOnlyList<IndexEvent>> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        // Wait for at least one event.
        IndexEvent? first;
        try
        {
            first = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return Array.Empty<IndexEvent>();
        }

        Absorb(first);

        // Now drain everything that's immediately available plus wait up to
        // the global batch window for more events.
        var deadline = DateTimeOffset.UtcNow + _globalBatchWindow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            delayCts.CancelAfter(remaining);

            try
            {
                while (_incoming.Reader.TryRead(out var evt))
                    Absorb(evt);

                var next = await _incoming.Reader.ReadAsync(delayCts.Token).ConfigureAwait(false);
                Absorb(next);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Batch window expired — emit what we have.
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
        }

        return Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _incoming.Writer.TryComplete();
    }

    private void Absorb(IndexEvent evt)
    {
        switch (evt)
        {
            case FileChanged fc:
                _pendingPaths[fc.RelativePath] = (fc, DateTimeOffset.UtcNow);
                break;

            case FileDeleted fd:
                _pendingPaths[fd.RelativePath] = (fd, DateTimeOffset.UtcNow);
                break;

            case ReconciliationRequested:
                // Reconciliation is idempotent: one pass diffs the index
                // against git and emits corrective events. Multiple
                // ReconciliationRequested events in the same batch are
                // redundant — every repair-queue enqueue from the executor
                // would otherwise add another one, and a single batch on a
                // freshly-started daemon with many stale rows could
                // accumulate dozens. Coalesce to one.
                if (!ContainsReconciliation(_pendingGlobal))
                    _pendingGlobal.Add(evt);
                break;

            case HeadMoved hm:
                // HeadMoved is idempotent in intent but the `OldHead` field
                // matters for the diff-tree: A→B→C must be processed as a
                // single diff from A to C, not two diffs (A→B then B→C) which
                // would walk the tree twice and re-upsert the B-only files.
                // If a HeadMoved is already pending, coalesce by keeping the
                // earliest `OldHead` and the latest `NewHead`. If none is
                // pending, add this one as-is.
                var existingIdx = FindFirstHeadMoved(_pendingGlobal);
                if (existingIdx >= 0)
                {
                    var existing = (HeadMoved)_pendingGlobal[existingIdx];
                    _pendingGlobal[existingIdx] = new HeadMoved(existing.OldHead, hm.NewHead);
                }
                else
                {
                    _pendingGlobal.Add(evt);
                }
                break;
        }
    }

    private static bool ContainsReconciliation(List<IndexEvent> events)
    {
        foreach (var e in events)
            if (e is ReconciliationRequested) return true;
        return false;
    }

    private static int FindFirstHeadMoved(List<IndexEvent> events)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is HeadMoved) return i;
        }
        return -1;
    }

    private IReadOnlyList<IndexEvent> Flush()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<IndexEvent>();

        // Emit global events first — HeadMoved should be processed before
        // per-path events so the diff-tree supersedes stale FSW events.
        // Apply _maxBatchSize across the whole emitted list so a batch
        // flooded with global events cannot balloon past the cap. Any
        // overflow stays in _pendingGlobal and will be emitted on the next
        // drain.
        var globalConsumed = 0;
        for (var i = 0; i < _pendingGlobal.Count && result.Count < _maxBatchSize; i++)
        {
            result.Add(_pendingGlobal[i]);
            globalConsumed++;
        }
        if (globalConsumed == _pendingGlobal.Count)
            _pendingGlobal.Clear();
        else
            _pendingGlobal.RemoveRange(0, globalConsumed);

        // If the global events alone filled the batch, defer per-path work
        // to the next drain rather than starving it entirely.
        if (result.Count >= _maxBatchSize)
            return result;

        // Emit per-path events that have settled past the per-path debounce.
        var ripe = new List<string>();
        foreach (var (path, entry) in _pendingPaths)
        {
            if (now - entry.Arrival >= _perPathDebounce)
                ripe.Add(path);
        }

        // If nothing is ripe but we have pending paths, emit all of them
        // anyway — the global batch window has expired so we shouldn't block
        // further.
        if (ripe.Count == 0 && _pendingPaths.Count > 0)
        {
            foreach (var path in _pendingPaths.Keys)
                ripe.Add(path);
        }

        // Sort for deterministic batch order.
        ripe.Sort(StringComparer.Ordinal);

        foreach (var path in ripe)
        {
            if (_pendingPaths.Remove(path, out var entry))
            {
                result.Add(entry.Event);
                if (result.Count >= _maxBatchSize) break;
            }
        }

        return result;
    }
}
