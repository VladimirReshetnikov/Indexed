using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Tests for <see cref="DebouncingEventQueue"/> — verifies per-path
/// deduplication, global batch windowing, and event ordering.
/// </summary>
public sealed class DebouncingEventQueueTests
{
    [Fact]
    public async Task SingleEvent_DrainedWithinBatchWindow()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        queue.Enqueue(new FileChanged("a.cs"));

        var batch = await queue.DequeueAsync();

        Assert.Single(batch);
        Assert.IsType<FileChanged>(batch[0]);
        Assert.Equal("a.cs", ((FileChanged)batch[0]).LogicalPath);
    }

    [Fact]
    public async Task RapidSamePathEvents_CollapsedToOne()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));

        // Rapid-fire 5 events for the same path.
        for (var i = 0; i < 5; i++)
            queue.Enqueue(new FileChanged("a.cs"));

        var batch = await queue.DequeueAsync();

        // Per-path debouncing collapses them into one.
        var aEvents = batch.OfType<FileChanged>().Where(e => e.LogicalPath == "a.cs").ToList();
        Assert.Single(aEvents);
    }

    [Fact]
    public async Task DifferentPaths_AllEmitted()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));

        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new FileChanged("b.cs"));
        queue.Enqueue(new FileChanged("c.cs"));

        var batch = await queue.DequeueAsync();

        Assert.Equal(3, batch.OfType<FileChanged>().Count());
    }

    [Fact]
    public async Task HeadMoved_EmittedBeforeFileEvents()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));

        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new HeadMoved("aaa", "bbb"));

        var batch = await queue.DequeueAsync();

        // HeadMoved should come first (global events before per-path).
        Assert.IsType<HeadMoved>(batch[0]);
    }

    [Fact]
    public async Task ReconciliationRequested_EmittedBeforeFileEvents()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));

        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new ReconciliationRequested());

        var batch = await queue.DequeueAsync();

        Assert.IsType<ReconciliationRequested>(batch[0]);
    }

    [Fact]
    public async Task FileDeleted_OverridesFileChanged_ForSamePath()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));

        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new FileDeleted("a.cs"));

        var batch = await queue.DequeueAsync();

        // The last event for the path wins — should be FileDeleted.
        var aEvent = batch.OfType<IndexEvent>()
            .FirstOrDefault(e => e is FileChanged fc && fc.LogicalPath == "a.cs"
                             || e is FileDeleted fd && fd.LogicalPath == "a.cs");
        Assert.IsType<FileDeleted>(aEvent);
    }

    [Fact]
    public async Task PendingCount_ReflectsQueueState()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, queue.PendingCount);

        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new FileChanged("b.cs"));

        // PendingCount is computed from absorbed items. Before DequeueAsync
        // consumes the channel, items are not yet absorbed — count stays 0.
        // After dequeue drains and flushes, count drops back to 0.
        var batch = await queue.DequeueAsync();
        Assert.True(batch.Count >= 1);

        // After dequeue, pending count should be 0 — all items were flushed.
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Complete_CausesDrainToReturnEmpty()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        queue.Complete();

        var batch = await queue.DequeueAsync();

        Assert.Empty(batch);
    }

    [Fact]
    public async Task CancellationToken_Respected()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // No events enqueued — DequeueAsync blocks until cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.DequeueAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task MaxBatchSize_CapsOutput()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100),
            maxBatchSize: 3);

        for (var i = 0; i < 10; i++)
            queue.Enqueue(new FileChanged($"file{i}.cs"));

        var batch = await queue.DequeueAsync();

        // Should cap at maxBatchSize (3) for per-path events.
        // Global events are separate but there are none here.
        Assert.True(batch.Count <= 3, $"Expected <= 3 events, got {batch.Count}");
    }

    [Fact]
    public async Task HeadMoved_Multiple_CoalesceToOne_PreservingOldestOldAndNewestNew()
    {
        // A→B→C should collapse to A→C so the indexer runs one diff-tree
        // that covers the whole range rather than two that re-visit B.
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        queue.Enqueue(new HeadMoved("aaa", "bbb"));
        queue.Enqueue(new HeadMoved("bbb", "ccc"));
        queue.Enqueue(new HeadMoved("ccc", "ddd"));

        var batch = await queue.DequeueAsync();

        var heads = batch.OfType<HeadMoved>().ToList();
        Assert.Single(heads);
        Assert.Equal("aaa", heads[0].OldHead);
        Assert.Equal("ddd", heads[0].NewHead);
    }

    [Fact]
    public async Task PendingCount_ReachesZero_AfterDrain()
    {
        // Regression: before the dedicated Interlocked counter, PendingCount
        // was computed from Dictionary.Count + List.Count — both of which
        // are NOT safe to observe while the single consumer thread mutates
        // the underlying collections. The visible count is now published
        // via Volatile.Write on the consumer thread after every absorb /
        // flush, so any cross-thread reader sees a stable scalar.
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 20; i++)
            queue.Enqueue(new FileChanged($"f{i}.cs"));

        // Drain — after Flush, PendingCount must fall to zero.
        await queue.DequeueAsync();

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task MaxBatchSize_AppliesToGlobalEvents_NotJustPerPath()
    {
        // Before the fix, _maxBatchSize was only checked inside the per-path
        // emission loop — a caller that dumped 50 HeadMoved events (e.g. a
        // noisy rebase + frequent reconciliation pings) would emit all 50 in
        // a single batch. Global events must respect the cap too.
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50),
            maxBatchSize: 2);

        // HeadMoved events with distinct SHA pairs coalesce to a single
        // running aggregate; to exercise the global-batch cap we enqueue a
        // mix of HeadMoved + ReconciliationRequested + per-path events.
        queue.Enqueue(new HeadMoved("aaa", "bbb"));
        queue.Enqueue(new ReconciliationRequested());
        queue.Enqueue(new FileChanged("a.cs"));
        queue.Enqueue(new FileChanged("b.cs"));
        queue.Enqueue(new FileChanged("c.cs"));

        var batch = await queue.DequeueAsync();

        Assert.True(batch.Count <= 2, $"Expected <= 2 events (maxBatchSize), got {batch.Count}");
    }
}
