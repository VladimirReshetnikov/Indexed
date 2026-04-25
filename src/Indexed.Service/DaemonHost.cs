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
using Indexed.Targets;
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
/// for <c>/search</c> and return cached metadata for everything else. The host
/// owns the per-target <see cref="SqliteIndex"/>: it opens <c>index.db</c>
/// during <see cref="StartAsync"/>, runs an initial full scan when required
/// (empty DB or schema mismatch), and wires incremental indexing
/// (<see cref="DirectoryWatcher"/>, optional <see cref="IRevisionTracker"/>,
/// and background reconciliation) before entering the request loop. <c>POST /rescan</c>
/// enqueues a reconciliation request and returns immediately; the actual work
/// happens asynchronously on the incremental indexer worker.
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
    /// Limits in-flight <em>search</em> request handlers. Admin endpoints
    /// (<c>/status</c>, <c>/shutdown</c>) bypass this gate via
    /// <see cref="_adminGate"/> so a saturated search workload cannot stall
    /// liveness probes or shutdown.
    /// </summary>
    private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private const int MaxConcurrentRequests = 8;

    /// <summary>
    /// Separate concurrency gate for admin endpoints — /status and /shutdown.
    /// Sized independently (and much smaller) so the admin path has its own
    /// reservation of threads even when <see cref="_requestGate"/> is fully
    /// occupied by concurrent /search handlers.
    /// </summary>
    private readonly SemaphoreSlim _adminGate = new(MaxConcurrentAdmin, MaxConcurrentAdmin);
    private const int MaxConcurrentAdmin = 4;

    private IIndexTarget? _target;
    private GitIndexTarget? _gitTarget;
    private GitRepository? _repo;
    private string? _repoId;
    private string? _targetId;
    private DaemonPaths? _paths;
    private Mutex? _singletonMutex;
    private HttpListener? _listener;
    private SqliteIndex? _index;
    private ISearchBackend? _backend;
    private IdleExitTimer? _idleTimer;
    private DaemonInfo? _info;
    private IndexStatistics? _lastScan;
    private IndexingOptions? _indexingOptions;
    private Task? _initialScanTask;
    private int _initialScanInProgress;
    private string? _initialScanError;
    private DebouncingEventQueue? _eventQueue;
    private IncrementalIndexer? _incrementalIndexer;
    private DirectoryWatcher? _directoryWatcher;
    private IRevisionTracker? _revisionTracker;
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
        var targetSelection = _options.TargetSelection ?? new TargetSelection
        {
            RepoRoot = _options.RepoRoot,
            IndexIncludeGlobs = _options.IndexIncludeGlobs,
            IndexExcludeGlobs = _options.IndexExcludeGlobs,
            UseDefaultIndexExcludes = _options.UseDefaultIndexExcludes,
            UseDefaultDirectoryExcludes = _options.UseDefaultDirectoryExcludes,
            MaxIndexableFileBytes = _options.MaxIndexableFileBytes,
        };

        _target = targetSelection.OpenTarget(cancellationToken);
        _gitTarget = _target as GitIndexTarget;
        _repo = _gitTarget?.Repository;
        _repoId = _gitTarget?.RepositoryId;
        _targetId = _target.TargetId;
        _paths = DaemonPaths.ForTarget(_targetId, _options.AppDataBase);
        _paths.EnsureCreated();

        if (_options.UseSingletonMutex && OperatingSystem.IsWindows())
            AcquireMutex();

        BindListener();

        _info = new DaemonInfo(
            Port: _listener!.Prefixes.Count == 0 ? 0 : ExtractPort(_listener),
            Pid: Environment.ProcessId,
            TargetKind: _target!.Spec.Kind,
            TargetId: _target.TargetId,
            Roots: _target.Roots,
            PrimaryRoot: _target.Roots[0],
            RepoRoot: _repo?.RepoRoot,
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

        if (_options.BackendOverride is not null)
        {
            _backend = _options.BackendOverride;
        }
        else
        {
            _indexingOptions = BuildEffectiveIndexingOptions(_target.Spec);

            _index = SqliteIndex.OpenOrCreate(_paths.IndexDbPath);
            _index.SetMeta(Indexed.Core.SqliteSchema.MetaKey_RepoId, _repoId);

            _eventQueue = new DebouncingEventQueue();

            // Schema v2: FTS5 is contentless — the backend reads live content
            // from disk at query time via FileContentProvider. Failed reads
            // (missing, unreadable, oversize) are fed back into the event
            // queue so the incremental indexer self-heals.
            _incrementalIndexer = new IncrementalIndexer(
                _target, _index, _eventQueue, _indexingOptions, _logger);

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

            _directoryWatcher = new DirectoryWatcher(
                _target, _eventQueue, _indexingOptions, _logger);
            _directoryWatcher.Start();

            if (_repo is not null)
            {
                _revisionTracker = new HeadPoller(
                    _repo, _index, _eventQueue, interval: null, _logger);
                _revisionTracker.Start();
            }

            _reconciliationScheduler = new ReconciliationScheduler(
                _eventQueue, interval: null, _logger);
            _reconciliationScheduler.Start();

            if (_options.RunInitialScan && _index.GetFileCount() == 0)
            {
                StartInitialScan(_indexingOptions);
            }

            var contentProvider = new FileContentProvider(_target, _indexingOptions.MaxIndexableFileBytes);
            _backend = new SqliteSearchBackend(_index, BuildFreshness, contentProvider, _eventQueue);
            _indexOptimizer.Start();
        }

        _logger.LogInformation(
            "daemon listening on port {Port}, targetId={TargetId}, pid={Pid}",
            _info.Port, _info.TargetId, _info.Pid);
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
                // Classify admin endpoints (/status, /shutdown) so they ride
                // the small, isolated _adminGate. A saturated search workload
                // on _requestGate must not be able to starve liveness probes
                // or block shutdown acknowledgement.
                var gate = IsAdminEndpoint(context.Request) ? _adminGate : _requestGate;
                _ = Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await HandleRequestSafelyAsync(context).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
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
        _directoryWatcher?.Dispose();
        _revisionTracker?.Dispose();
        _reconciliationScheduler?.Dispose();

        if (_incrementalIndexer is not null)
        {
            try { await _incrementalIndexer.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _eventQueue?.Dispose();

        if (_initialScanTask is not null)
        {
            try { await _initialScanTask.ConfigureAwait(false); } catch { }
            _initialScanTask = null;
        }

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
        _adminGate.Dispose();
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
                    new ErrorResponse(IndexedErrorCode.Forbidden, "shutdown token missing or invalid"),
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
            RepoRoot: _gitTarget?.Repository.RepoRoot,
            RepoId: _repoId,
            StartedAt: _startedAt,
            Freshness: BuildFreshness(),
            Optimizer: BuildOptimizerStats(),
            Index: BuildIndexStatus(),
            TargetKind: _target!.Spec.Kind,
            TargetId: _target.TargetId,
            Roots: _target.Roots,
            PrimaryRoot: _target.Roots[0]);

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

    private IndexStatus? BuildIndexStatus()
    {
        if (_index is null || _indexingOptions is null)
            return null;

        var skips = _index.GetSkipStats();
        return new IndexStatus(
            IndexedFileCount: _index.GetFileCount(),
            MaxIndexableFileBytes: _indexingOptions.MaxIndexableFileBytes,
            InitialScanInProgress: Volatile.Read(ref _initialScanInProgress) != 0,
            IncludeGlobs: _indexingOptions.IncludeGlobs,
            ExcludeGlobs: _indexingOptions.ExcludeGlobs,
            Skips: skips,
            InitialScanError: _initialScanError);
    }

    private Freshness BuildFreshness()
    {
        var revisionKind = _target?.Spec.Kind == TargetKind.GitRepository
            ? RevisionKind.GitHead
            : RevisionKind.None;

        string? revisionToken = _revisionTracker?.LastKnownRevisionToken;
        if (string.IsNullOrEmpty(revisionToken))
        {
            try { revisionToken = _target?.GetCurrentRevisionToken(); }
            catch { /* transient target error — leave as null */ }
        }

        string? indexedRevisionToken = null;
        DateTimeOffset? lastFullScan = null;
        DateTimeOffset? lastReconciliation = null;
        string? note = null;

        if (_index is null)
        {
            note = "test backend override in use; no index present.";
        }
        else
        {
            indexedRevisionToken = _index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
            if (string.IsNullOrEmpty(indexedRevisionToken)) indexedRevisionToken = null;

            var lastScanRaw = _index.GetMeta(SqliteSchema.MetaKey_LastFullScanAt);
            if (!string.IsNullOrEmpty(lastScanRaw)
                && long.TryParse(lastScanRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastScanUnix))
            {
                lastFullScan = DateTimeOffset.FromUnixTimeSeconds(lastScanUnix);
            }

            var lastReconciliationRaw = _index.GetMeta(SqliteSchema.MetaKey_LastReconciliationAt);
            if (!string.IsNullOrEmpty(lastReconciliationRaw)
                && long.TryParse(lastReconciliationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastReconciliationUnix))
            {
                lastReconciliation = DateTimeOffset.FromUnixTimeSeconds(lastReconciliationUnix);
            }
        }

        var currentRevisionToken = string.IsNullOrEmpty(revisionToken) ? null : revisionToken;
        var pendingCount = _eventQueue?.PendingCount ?? 0;
        // Fold the indexer's in-flight flag in so a caller that happens to
        // read /status between DequeueAsync (decrements pendingCount) and
        // the transaction commit still sees isStale=true. Without this the
        // freshness window briefly reports isStale=false for work that has
        // not yet been applied.
        var inFlight = _incrementalIndexer?.IsProcessingBatch ?? false;
        var initialScanInProgress = Volatile.Read(ref _initialScanInProgress) != 0;
        var isStale = pendingCount > 0 || inFlight || initialScanInProgress || _initialScanError is not null;
        if (revisionKind != RevisionKind.None)
        {
            isStale = isStale
                || indexedRevisionToken is null
                || currentRevisionToken is null
                || !string.Equals(indexedRevisionToken, currentRevisionToken, StringComparison.Ordinal);
        }

        if (initialScanInProgress)
            note = "initial full scan is still running.";
        else if (_initialScanError is not null)
            note = $"initial full scan failed: {_initialScanError}";

        return new Freshness(
            IndexedHead: indexedRevisionToken,
            CurrentHead: currentRevisionToken,
            PendingFileCount: pendingCount,
            LastFullScanAt: lastFullScan,
            IsStale: isStale,
            Note: note,
            IndexedRevisionToken: indexedRevisionToken,
            CurrentRevisionToken: currentRevisionToken,
            RevisionKind: revisionKind,
            LastReconciliationAt: lastReconciliation);
    }

    private void StartInitialScan(IndexingOptions indexingOptions)
    {
        if (_target is null || _index is null)
            throw new InvalidOperationException("target and index must be initialized before starting a full scan");

        _logger.LogInformation("index is empty; scheduling initial full scan");
        Volatile.Write(ref _initialScanInProgress, 1);
        _initialScanError = null;

        _initialScanTask = Task.Run(async () =>
        {
            var scanStarted = Stopwatch.StartNew();
            try
            {
                var indexer = new FullScanIndexer(_target, _index, indexingOptions, _logger);
                _lastScan = await indexer.RunAsync(progress: null, _shutdownCts.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "initial full scan complete: indexed={Indexed} skipped={Skipped} filtered={Filtered} unchanged={Unchanged} total={Total} elapsed={ElapsedMs}ms",
                    _lastScan.Indexed, _lastScan.Skipped, _lastScan.Filtered, _lastScan.Unchanged, _lastScan.Total,
                    scanStarted.ElapsedMilliseconds);
                _indexOptimizer?.NotifyDirty();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("initial full scan canceled");
            }
            catch (Exception ex)
            {
                _initialScanError = ex.GetType().Name + ": " + ex.Message;
                _logger.LogError(ex, "initial full scan failed");
            }
            finally
            {
                Volatile.Write(ref _initialScanInProgress, 0);
                _idleTimer?.Poke();
            }
        });
    }

    private static IndexingOptions BuildEffectiveIndexingOptions(TargetSpec spec)
    {
        IReadOnlyList<string>? excludes = spec.IndexExcludeGlobs;
        if (spec.UseDefaultIndexExcludes)
            excludes = ExcludeFilter.Combine(excludes, ExcludeFilter.DefaultBinaryAdjacentGlobs);
        if (spec.UseDefaultDirectoryExcludes)
            excludes = ExcludeFilter.Combine(excludes, ExcludeFilter.DefaultDirectoryModeExcludes);
        return new IndexingOptions(spec.IndexIncludeGlobs, excludes, spec.MaxIndexableFileBytes);
    }

    private void AcquireMutex()
    {
#pragma warning disable CA1416 // Mutex name with "Global\" prefix is Windows-only; gated above.
        var name = $"Global\\Indexed-{_targetId}";
        _singletonMutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        try
        {
            if (!_singletonMutex.WaitOne(TimeSpan.Zero))
            {
                _singletonMutex.Dispose();
                _singletonMutex = null;
                throw new DaemonAlreadyRunningException(_targetId!);
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

    /// <summary>
    /// Classify a request as an admin-plane endpoint. Admin endpoints bypass
    /// the search-request gate so liveness and shutdown remain responsive
    /// when /search is saturated. The classification is by URL prefix only
    /// — it runs before the handler resolves the route, so it must match
    /// exactly the same set of paths that <c>HandleRequestAsync</c> treats
    /// as admin.
    /// </summary>
    private static bool IsAdminEndpoint(HttpListenerRequest request)
    {
        var path = request.Url?.AbsolutePath;
        return path is "/status" or "/shutdown";
    }

    // Keep the switch branches in sync with every member of IndexedErrorCode;
    // a new code should surface either a sensible HTTP status here or fall
    // through to 500 deliberately. Adding a new code without touching this
    // table would silently return 500 — which is recoverable in practice
    // but an observability regression (CLI can't branch).
    private static int MapErrorCodeToHttp(IndexedErrorCode code) => code switch
    {
        IndexedErrorCode.BadRequest => 400,
        IndexedErrorCode.PatternInvalid => 400,
        IndexedErrorCode.Forbidden => 403,
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
/// already holds the single-instance mutex for the target.
/// </summary>
public sealed class DaemonAlreadyRunningException : Exception
{
    public string TargetId { get; }

    public DaemonAlreadyRunningException(string targetId)
        : base($"another Indexed daemon is already running for targetId={targetId}")
    {
        TargetId = targetId;
    }
}
