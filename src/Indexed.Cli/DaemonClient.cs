using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Service;

namespace Indexed.Cli;

/// <summary>
/// HTTP client that talks to a running <c>Indexed.Service</c> daemon.
/// </summary>
/// <remarks>
/// <para>
/// Construction flow: the CLI resolves the repo root, computes <c>repoId</c>,
/// locates <c>daemon.json</c>, and hands it to <see cref="CreateAsync"/>.
/// If the port-file is absent or pointing at a defunct daemon, the client
/// asks <see cref="DaemonLauncher"/> to spawn a new process and blocks until
/// <c>/status</c> answers.
/// </para>
/// <para>
/// Every method returns either a typed response or an <see cref="ErrorResponse"/>
/// — the client translates HTTP-level failures (connection refused, 5xx) into
/// synthetic <see cref="ErrorResponse"/> objects so CLI callers have a single
/// error-handling path.
/// </para>
/// </remarks>
internal sealed class DaemonClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly DaemonInfo _info;

    private DaemonClient(DaemonInfo info, HttpClient http)
    {
        _info = info;
        _http = http;
    }

    /// <summary>
    /// Bound port of the daemon this client is talking to.
    /// </summary>
    public int Port => _info.Port;

    /// <summary>
    /// PID of the daemon this client is talking to.
    /// </summary>
    public int DaemonPid => _info.Pid;

    /// <summary>
    /// Connect to an existing daemon or spawn one via
    /// <see cref="DaemonLauncher"/>.
    /// </summary>
    /// <param name="repoRoot">Repository working-tree root.</param>
    /// <param name="appData">Override for <c>%APPDATA%\Indexed</c>.</param>
    /// <param name="startupTimeout">How long to wait for the daemon to become ready (default 120 s).</param>
    /// <param name="indexExcludeGlobs">Additional index-time exclude globs forwarded on launch.</param>
    /// <param name="noDefaultExcludes">
    /// When <c>true</c>, pass <c>--no-default-excludes</c> to the daemon on
    /// launch so the built-in exclude list is not applied.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DaemonClient> CreateAsync(
        string repoRoot,
        string? appData = null,
        TimeSpan? startupTimeout = null,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool noDefaultExcludes = false,
        CancellationToken cancellationToken = default)
    {
        var repo = Indexed.Git.GitRepository.Open(repoRoot);
        var repoId = RepoId.Compute(repo.RepoRoot, repo.GetFirstCommitSha());
        var paths = DaemonPaths.ForRepo(repoId, appData);
        paths.EnsureCreated();

        var info = DaemonInfo.TryRead(paths.DaemonJsonPath);
        if (info is not null && await PingAsync(info, cancellationToken).ConfigureAwait(false))
            return Build(info);

        DaemonInfo.TryDelete(paths.DaemonJsonPath);

        // Cold start includes the initial full scan (proposal §14 targets
        // ≤60 s for this repo). Give the daemon generous headroom here; the
        // caller can still override via startupTimeout for tests.
        var launched = await DaemonLauncher.LaunchAsync(
            repo.RepoRoot,
            paths.DaemonJsonPath,
            startupTimeout ?? TimeSpan.FromSeconds(120),
            appData,
            indexExcludeGlobs,
            noDefaultExcludes,
            cancellationToken).ConfigureAwait(false);

        if (launched is null)
            throw new InvalidOperationException("daemon failed to start within the configured timeout");

        if (!await PingAsync(launched, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("daemon started but is not responding to /status");

        return Build(launched);
    }

    /// <summary>Adopt an already-running daemon described by <paramref name="info"/>.</summary>
    public static DaemonClient FromInfo(DaemonInfo info) => Build(info);

    private static DaemonClient Build(DaemonInfo info)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{info.Port}/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        return new DaemonClient(info, http);
    }

    public async Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("status", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, IndexedJsonContext.Default.StatusResponse, ct)
            .ConfigureAwait(false);
    }

    public async Task<(SearchResponse? ok, ErrorResponse? err)> SearchAsync(
        SearchRequest request,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "search", request, IndexedJsonContext.Default.SearchRequest, ct)
            .ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var ok = await JsonSerializer
                .DeserializeAsync(stream, IndexedJsonContext.Default.SearchResponse, ct)
                .ConfigureAwait(false);
            return (ok, null);
        }

        var err = await JsonSerializer
            .DeserializeAsync(stream, IndexedJsonContext.Default.ErrorResponse, ct)
            .ConfigureAwait(false);
        return (null, err);
    }

    public async Task<bool> RescanAsync(CancellationToken ct = default)
    {
        using var content = new StringContent(string.Empty);
        using var response = await _http.PostAsync("rescan", content, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ShutdownAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "shutdown");
        req.Headers.Add("X-Indexed-Shutdown-Token", _info.ShutdownToken);
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static async Task<bool> PingAsync(DaemonInfo info, CancellationToken ct)
    {
        try
        {
            using var probe = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{info.Port}/"),
                Timeout = TimeSpan.FromSeconds(2),
            };
            using var response = await probe.GetAsync("status", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public void Dispose() => _http.Dispose();
}
