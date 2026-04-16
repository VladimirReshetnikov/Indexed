using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Indexed.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Service;

/// <summary>
/// Long-running daemon that serves Indexed HTTP requests for a single
/// repository.
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities: single-instance enforcement via a named mutex, port
/// binding on <c>127.0.0.1:0</c>, <c>daemon.json</c> atomic write, HTTP
/// request dispatch, idle-exit scheduling, and graceful shutdown.
/// </para>
/// <para>
/// Request handlers are cheap — they delegate to an <see cref="ISearchBackend"/>
/// for <c>/search</c> and return cached metadata for everything else. Stage 2
/// adds ownership of the per-repo <see cref="SqliteIndex"/>: the host opens
/// <c>index.db</c> during <see cref="StartAsync"/>, runs a synchronous full
/// scan if the DB is empty, and disposes the index on shutdown. No watcher is
/// wired yet — <c>POST /rescan</c> runs another full scan on the request
/// thread; Stage 4 moves that to a background worker with incremental diffs.
/// </para>
/// <para>
/// Lifecycle:
/// </para>
/// <list type="number">
///   <item><description><see cref="StartAsync"/> acquires the mutex, binds the listener, writes <c>daemon.json</c>, and returns once listening.</description></item>
///   <item><description><see cref="RunAsync"/> blocks on the request loop until cancellation or an internal shutdown signal.</description></item>
///   <item><description>Shutdown removes <c>daemon.json</c>, disposes the listener, and releases the mutex.</description></item>
/// </list>
/// </remarks>
internal sealed class DaemonHost : IAsyncDisposable
{
    private readonly DaemonOptions _options;
    private readonly ILogger<DaemonHost> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Limits in-flight request handlers to avoid unbounded <c>Task.Run</c>
    /// fanout under load (e.g., agent hammering with concurrent searches).
    /// </summary>
    private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private const int MaxConcurrentRequests = 8;

    private GitRepository? _repo;
    private string? _repoId;
    private DaemonPaths? _paths;
    private Mutex? _singletonMutex;
    private HttpListener? _listener;
    private SqliteIndex? _index;
    private ISearchBackend? _backend;
    private IdleExitTimer? _idleTimer;
    private DaemonInfo? _info;
    private IndexStatistics? _lastScan;
    private DebouncingEventQueue? _eventQueue;
    private IncrementalIndexer? _incrementalIndexer;
    private RepoWatcher? _repoWatcher;
    private HeadPoller? _headPoller;
    private ReconciliationScheduler? _reconciliationScheduler;
    private IndexOptimizer? _indexOptimizer;

    public DaemonHost(DaemonOptions options, ILogger<DaemonHost>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<DaemonHost>.Instance;
    }

    /// <summary>
    /// Information advertised in <c>daemon.json</c>. Available after
    /// <see cref="StartAsync"/> completes successfully.
    /// </summary>
    public DaemonInfo Info
        => _info ?? throw new InvalidOperationException("StartAsync has not completed");

    /// <summary>
    /// Acquire the single-instance mutex, bind the listener, write
    /// <c>daemon.json</c>. Returns once the listener is accepting connections.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="DaemonAlreadyRunningException"/> when another
    /// daemon already holds the mutex for the same <see cref="RepoId"/>.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _repo = GitRepository.Open(_options.RepoRoot);
        _repoId = RepoId.Compute(_repo.RepoRoot, _repo.GetFirstCommitSha());
        _paths = DaemonPaths.ForRepo(_repoId, _options.AppDataBase);
        _paths.EnsureCreated();

        if (_options.UseSingletonMutex && OperatingSystem.IsWindows())
            AcquireMutex();

        BindListener();

        if (_options.BackendOverride is not null)
        {
            _backend = _options.BackendOverride;
        }
        else
        {
            // Merge user-supplied exclude globs with the built-in defaults
            // (lockfiles, minified bundles, generated C#, …) unless the caller
            // has opted out via UseDefaultIndexExcludes = false.
            var effectiveExcludes = _options.UseDefaultIndexExcludes
                ? ExcludeFilter.Combine(_options.IndexExcludeGlobs, ExcludeFilter.DefaultBinaryAdjacentGlobs)
                : _options.IndexExcludeGlobs;

            _index = SqliteIndex.OpenOrCreate(_paths.IndexDbPath);
            _index.SetMeta(Indexed.Core.SqliteSchema.MetaKey_RepoId, _repoId);

            if (_options.RunInitialScan && _index.GetFileCount() == 0)
            {
                _logger.LogInformation("index is empty; running full scan");
                var scanStarted = Stopwatch.StartNew();
                var indexer = new FullScanIndexer(_repo, _index, effectiveExcludes, _logger);
                _lastScan = await indexer.RunAsync(progress: null, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "initial full scan complete: indexed={Indexed} skipped={Skipped} unchanged={Unchanged} total={Total} elapsed={ElapsedMs}ms",
                    _lastScan.Indexed, _lastScan.Skipped, _lastScan.Unchanged, _lastScan.Total,
                    scanStarted.ElapsedMilliseconds);
            }

            // Stage 4: start the incremental indexer pipeline.
            _eventQueue = new DebouncingEventQueue();

            // Schema v2: FTS5 is contentless — the backend reads live content
            // from disk at query time via FileContentProvider. Failed reads
            // (missing, unreadable, oversize) are fed back into the event
            // queue so the incremental indexer self-heals.
            var contentProvider = new FileContentProvider(_repo.RepoRoot);
            _backend = new SqliteSearchBackend(_index, BuildFreshness, contentProvider, _eventQueue);

            _incrementalIndexer = new IncrementalIndexer(
                _repo, _index, _eventQueue, effectiveExcludes, _logger);

            // Background FTS5 segment merger. Subscribes to BatchCommitted so
            // it only runs during idle periods that followed real write work.
            _indexOptimizer = new IndexOptimizer(
                _index,
                _options.OptimizerInterval,
                _options.OptimizerPageBudget,
                _logger);

            _incrementalIndexer.BatchCommitted += () => _idleTimer?.Poke();
            _incrementalIndexer.BatchCommitted += () => _indexOptimizer?.NotifyDirty();
            _incrementalIndexer.Start();
            _indexOptimizer.Start();

            _repoWatcher = new RepoWatcher(
                _repo.RepoRoot, _eventQueue, effectiveExcludes, _logger);
            _repoWatcher.Start();

            _headPoller = new HeadPoller(
                _repo, _index, _eventQueue, interval: null, _logger);
            _headPoller.Start();

            _reconciliationScheduler = new ReconciliationScheduler(
                _eventQueue, interval: null, _logger);
            _reconciliationScheduler.Start();
        }

        _info = new DaemonInfo(
            Port: _listener!.Prefixes.Count == 0 ? 0 : ExtractPort(_listener),
            Pid: Environment.ProcessId,
            RepoRoot: _repo.RepoRoot,
            RepoId: _repoId,
            StartedAt: _startedAt,
            DaemonVersion: _options.DaemonVersion,
            ShutdownToken: DaemonInfo.NewShutdownToken());

        _info.WriteAtomic(_paths.DaemonJsonPath);
        _idleTimer = new IdleExitTimer(_options.IdleTimeout, () =>
        {
            _logger.LogInformation("idle-exit window elapsed; requesting shutdown");
            RequestShutdown();
        });

        _logger.LogInformation(
            "daemon listening on port {Port}, repoId={RepoId}, pid={Pid}",
            _info.Port, _info.RepoId, _info.Pid);
    }

    /// <summary>
    /// Process requests until cancellation or an internal shutdown signal.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null) throw new InvalidOperationException("call StartAsync first");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(ex, "HttpListener error — stopping request loop");
                break;
            }

            _idleTimer?.Poke();
            try
            {
                _ = Task.Run(async () =>
                {
                    await _requestGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await HandleRequestSafelyAsync(context).ConfigureAwait(false);
                    }
                    finally
                    {
                        _requestGate.Release();
                    }
                }, ct);
            }
            catch (ObjectDisposedException)
            {
                // CTS disposed during shutdown — ignore; the listener loop
                // will exit on the next iteration.
            }
        }
    }

    /// <summary>
    /// Request cooperative shutdown. Idempotent and non-blocking; callers
    /// should <c>await</c> <see cref="DisposeAsync"/> to observe completion.
    /// </summary>
    public void RequestShutdown()
    {
        if (!_shutdownCts.IsCancellationRequested) _shutdownCts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        try { _shutdownCts.Cancel(); } catch { }

        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }

        if (_paths is not null) DaemonInfo.TryDelete(_paths.DaemonJsonPath);

        _idleTimer?.Dispose();

        // Stage 4: stop watcher/poller/scheduler first, then drain the
        // incremental indexer worker, then close the index.
        _repoWatcher?.Dispose();
        _headPoller?.Dispose();
        _reconciliationScheduler?.Dispose();

        if (_incrementalIndexer is not null)
        {
            try { await _incrementalIndexer.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _eventQueue?.Dispose();

        // Dispose the optimizer AFTER the incremental indexer so the final
        // merge sees the last batch commit's dirty flag. Runs BEFORE the
        // index is disposed — the final merge opens a writer scope.
        if (_indexOptimizer is not null)
        {
            try { await _indexOptimizer.DisposeAsync().ConfigureAwait(false); } catch { }
            _indexOptimizer = null;
        }

        if (_index is not null)
        {
            try { await _index.DisposeAsync().ConfigureAwait(false); } catch { }
            _index = null;
        }

        try
        {
            _singletonMutex?.ReleaseMutex();
        }
        catch (ApplicationException) { /* not held by this thread */ }
        _singletonMutex?.Dispose();

        _requestGate.Dispose();
        _shutdownCts.Dispose();
    }

    // ----- request handling -----

    private async Task HandleRequestSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "unhandled error in request handler");
            try
            {
                await WriteJsonAsync(
                    context.Response,
                    500,
                    new ErrorResponse(IndexedErrorCode.Internal, "unhandled server error", ex.GetType().Name),
                    IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        if (method == "GET" && path == "/status")
        {
            await WriteJsonAsync(
                context.Response, 200, BuildStatus(), IndexedJsonContext.Default.StatusResponse)
                .ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/search")
        {
            SearchRequest? request;
            try
            {
                // Cap request body to 64 KB to reject oversized payloads
                // before they reach the JSON parser.
                const int maxBodyBytes = 64 * 1024;
                var body = req.InputStream;
                if (req.ContentLength64 > maxBodyBytes)
                {
                    await WriteJsonAsync(
                        context.Response, 400,
                        new ErrorResponse(IndexedErrorCode.BadRequest,
                            $"request body too large ({req.ContentLength64} bytes, limit {maxBodyBytes})"),
                        IndexedJsonContext.Default.ErrorResponse)
                        .ConfigureAwait(false);
                    return;
                }
                var limitedStream = new LengthLimitingStream(body, maxBodyBytes);

                request = await JsonSerializer
                    .DeserializeAsync(limitedStream, IndexedJsonContext.Default.SearchRequest, _shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(
                    context.Response, 400,
                    new ErrorResponse(IndexedErrorCode.BadRequest, "malformed JSON body", ex.Message),
                    IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("byte limit"))
            {
                await WriteJsonAsync(
                    context.Response, 400,
                    new ErrorResponse(IndexedErrorCode.BadRequest, "request body too large"),
                    IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                await WriteJsonAsync(
                    context.Response, 400,
                    new ErrorResponse(IndexedErrorCode.BadRequest, "empty request body"),
                    IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
                return;
            }

            var result = await _backend!
                .SearchAsync(request, _shutdownCts.Token)
                .ConfigureAwait(false);

            if (result.Error is not null)
            {
                var status = MapErrorCodeToHttp(result.Error.Code);
                await WriteJsonAsync(context.Response, status, result.Error, IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                context.Response, 200, result.Response!, IndexedJsonContext.Default.SearchResponse)
                .ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/rescan")
        {
            // Stage 4: enqueue a ReconciliationRequested event and return
            // immediately. The incremental indexer processes it asynchronously.
            _eventQueue?.Enqueue(new ReconciliationRequested());
            await WriteJsonAsync(
                context.Response, 200, BuildStatus(), IndexedJsonContext.Default.StatusResponse)
                .ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/shutdown")
        {
            var token = req.Headers["X-Indexed-Shutdown-Token"];
            if (!string.Equals(token, _info?.ShutdownToken, StringComparison.Ordinal))
            {
                await WriteJsonAsync(
                    context.Response, 403,
                    new ErrorResponse(IndexedErrorCode.Unavailable, "shutdown token missing or invalid"),
                    IndexedJsonContext.Default.ErrorResponse)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 204;
            context.Response.OutputStream.Close();
            _logger.LogInformation("shutdown requested by authenticated client");
            RequestShutdown();
            return;
        }

        await WriteJsonAsync(
            context.Response, 404,
            new ErrorResponse(IndexedErrorCode.BadRequest, $"unknown route: {method} {path}"),
            IndexedJsonContext.Default.ErrorResponse)
            .ConfigureAwait(false);
    }

    // ----- helpers -----

    private StatusResponse BuildStatus()
        => new(
            DaemonVersion: _options.DaemonVersion,
            SchemaVersion: _index?.SchemaVersion ?? 0,
            Pid: Environment.ProcessId,
            RepoRoot: _repo!.RepoRoot,
            RepoId: _repoId!,
            StartedAt: _startedAt,
            Freshness: BuildFreshness(),
            Optimizer: BuildOptimizerStats());

    /// <summary>
    /// Project <see cref="IndexOptimizer"/> counters into the serializable
    /// DTO, or <c>null</c> when no optimizer is wired (test backend
    /// override). All reads go through <c>IndexOptimizer</c>'s
    /// <c>Volatile.Read</c>/<c>Interlocked.Read</c> accessors so this method
    /// is safe to call from any HTTP request thread concurrently with an
    /// in-flight merge.
    /// </summary>
    private OptimizerStats? BuildOptimizerStats()
    {
        var opt = _indexOptimizer;
        if (opt is null) return null;
        return new OptimizerStats(
            MergeCount: opt.MergeCount,
            LastMergeAtUtc: opt.LastMergeAtUtc,
            LastMergeElapsedMs: opt.LastMergeElapsedMs);
    }

    private Freshness BuildFreshness()
    {
        string? head = _headPoller?.LastKnownHead;
        if (string.IsNullOrEmpty(head))
        {
            try { head = _repo!.GetHeadSha(); }
            catch { /* transient git error — leave as null */ }
        }

        string? indexedHead = null;
        DateTimeOffset? lastFullScan = null;
        string? note = null;

        if (_index is null)
        {
            note = "test backend override in use; no index present.";
        }
        else
        {
            indexedHead = _index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
            if (string.IsNullOrEmpty(indexedHead)) indexedHead = null;

            var lastScanRaw = _index.GetMeta(SqliteSchema.MetaKey_LastFullScanAt);
            if (!string.IsNullOrEmpty(lastScanRaw)
                && long.TryParse(lastScanRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastScanUnix))
            {
                lastFullScan = DateTimeOffset.FromUnixTimeSeconds(lastScanUnix);
            }
        }

        var currentHead = string.IsNullOrEmpty(head) ? null : head;
        var pendingCount = _eventQueue?.PendingCount ?? 0;
        // Fold the indexer's in-flight flag in so a caller that happens to
        // read /status between DequeueAsync (decrements pendingCount) and
        // the transaction commit still sees isStale=true. Without this the
        // freshness window briefly reports isStale=false for work that has
        // not yet been applied.
        var inFlight = _incrementalIndexer?.IsProcessingBatch ?? false;
        var isStale = indexedHead is null
            || currentHead is null
            || !string.Equals(indexedHead, currentHead, StringComparison.Ordinal)
            || pendingCount > 0
            || inFlight;

        return new Freshness(
            IndexedHead: indexedHead,
            CurrentHead: currentHead,
            PendingFileCount: pendingCount,
            LastFullScanAt: lastFullScan,
            IsStale: isStale,
            Note: note);
    }

    private void AcquireMutex()
    {
#pragma warning disable CA1416 // Mutex name with "Global\" prefix is Windows-only; gated above.
        var name = $"Global\\Indexed-{_repoId}";
        _singletonMutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        try
        {
            if (!_singletonMutex.WaitOne(TimeSpan.Zero))
            {
                _singletonMutex.Dispose();
                _singletonMutex = null;
                throw new DaemonAlreadyRunningException(_repoId!);
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous daemon crashed (taskkill /f, power loss) without
            // releasing the mutex. WaitOne still acquired it — we now own
            // it and can proceed.
            _logger.LogWarning("acquired abandoned daemon mutex — previous instance likely crashed");
        }
        _ = createdNew; // suppress unused local warning
#pragma warning restore CA1416
    }

    /// <summary>
    /// Bind an <see cref="HttpListener"/> to an OS-assigned ephemeral port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpListener"/> cannot bind port 0, so we pre-discover a
    /// free port via <see cref="TcpListener"/>. Between closing the TCP
    /// socket and opening the HTTP prefix another process may claim the same
    /// port (TOCTOU race). The retry loop makes this practically reliable.
    /// </para>
    /// </remarks>
    private void BindListener()
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Ask the OS for an ephemeral port by pre-opening a TcpListener on
            // 127.0.0.1:0. HttpListener can't bind port 0 directly.
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                return;
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "port {Port} was claimed between discovery and bind (attempt {Attempt}/{Max}); retrying",
                    port, attempt, maxAttempts);
                try { listener.Close(); } catch { }
            }
        }
    }

    private static int ExtractPort(HttpListener listener)
    {
        foreach (var prefix in listener.Prefixes)
        {
            if (Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
                return uri.Port;
        }
        return 0;
    }

    private static int MapErrorCodeToHttp(IndexedErrorCode code) => code switch
    {
        IndexedErrorCode.BadRequest => 400,
        IndexedErrorCode.PatternInvalid => 400,
        IndexedErrorCode.TimeoutExceeded => 504,
        IndexedErrorCode.RepoNotFound => 503,
        IndexedErrorCode.Unavailable => 503,
        IndexedErrorCode.NotImplemented => 501,
        IndexedErrorCode.Internal => 500,
        _ => 500,
    };

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        int status,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, typeInfo).ConfigureAwait(false);
    }
}

/// <summary>
/// Thrown by <see cref="DaemonHost.StartAsync"/> when another daemon process
/// already holds the single-instance mutex for the repo.
/// </summary>
public sealed class DaemonAlreadyRunningException : Exception
{
    public string RepoId { get; }

    public DaemonAlreadyRunningException(string repoId)
        : base($"another Indexed daemon is already running for repoId={repoId}")
    {
        RepoId = repoId;
    }
}
