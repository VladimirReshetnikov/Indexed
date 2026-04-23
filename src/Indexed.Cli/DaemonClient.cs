using System;
using System.Diagnostics;
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
/// Construction flow: the CLI resolves a target selection, computes the
/// corresponding target identity, locates <c>daemon.json</c>, and hands it to
/// <see cref="CreateAsync"/>. If the port-file is absent or pointing at a
/// defunct daemon, the client asks <see cref="DaemonLauncher"/> to spawn a new
/// process and blocks until <c>/status</c> answers.
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
    /// <param name="targetSelection">Target selection the daemon should serve.</param>
    /// <param name="appData">Override for <c>%LOCALAPPDATA%\Indexed</c>.</param>
    /// <param name="startupTimeout">How long to wait for the daemon to become ready (default 120 s).</param>
    /// <param name="idleTimeoutSeconds">
    /// Optional idle-exit override forwarded to the daemon on launch. Only
    /// applies when this call launches a new daemon.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DaemonClient> CreateAsync(
        TargetSelection targetSelection,
        string? appData = null,
        TimeSpan? startupTimeout = null,
        int? idleTimeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (targetSelection is null) throw new ArgumentNullException(nameof(targetSelection));

        var target = targetSelection.OpenTarget(cancellationToken);
        var paths = DaemonPaths.ForTarget(target.TargetId, appData);
        paths.EnsureCreated();

        var info = DaemonInfo.TryRead(paths.DaemonJsonPath);
        if (info is not null)
        {
            // Adoption probe: quick /status first. If the daemon is simply
            // busy (long-running search, post-startup scan storm) the 2 s
            // timeout can expire even though the process is healthy; verify
            // liveness via the PID before orphaning and retry with a longer
            // deadline. Only delete daemon.json when the PID is known dead
            // or a generous retry still fails.
            if (await PingWithLivenessAsync(info, cancellationToken).ConfigureAwait(false))
                return Build(info);

            DaemonInfo.TryDelete(paths.DaemonJsonPath);
        }

        // Cold start includes the initial full scan (proposal §14 targets
        // ≤60 s for this repo). Give the daemon generous headroom here; the
        // caller can still override via startupTimeout for tests.
        var launched = await DaemonLauncher.LaunchAsync(
            target.Spec,
            paths.DaemonJsonPath,
            startupTimeout ?? TimeSpan.FromSeconds(120),
            appData,
            idleTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (launched is null)
            throw new InvalidOperationException("daemon failed to start within the configured timeout");

        if (!await PingAsync(launched, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Ping the daemon at <paramref name="info"/> with a short-first,
    /// long-second retry policy gated by a PID-liveness check. Returns
    /// <c>true</c> only when the daemon answers <c>/status</c> with 2xx.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering: (1) short 2 s ping — fast path for healthy daemons; (2) if
    /// that fails, inspect the OS process table for <c>info.Pid</c>; a dead
    /// PID means the daemon.json is stale and we give up immediately so the
    /// caller can relaunch; (3) if the PID is alive (or liveness cannot be
    /// determined — e.g. cross-user ACL denial), retry once with a 10 s
    /// deadline to tolerate transient load spikes.
    /// </para>
    /// <para>
    /// PID reuse: on Windows and POSIX the OS does recycle pids, so we also
    /// require the process's <c>StartTime</c> to be at or before the
    /// <c>StartedAt</c> recorded in daemon.json. A newer process under the
    /// same pid cannot match. When <c>StartTime</c> is unreadable
    /// (<see cref="InvalidOperationException"/> on exited processes, ACL
    /// denial) we conservatively treat the PID as alive so a legitimately
    /// healthy daemon is not evicted.
    /// </para>
    /// </remarks>
    private static async Task<bool> PingWithLivenessAsync(DaemonInfo info, CancellationToken ct)
    {
        if (await PingAsync(info, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            return true;

        if (!IsDaemonPidAlive(info))
            return false;

        // Process is alive — give it a longer window to answer. This covers
        // the "daemon is mid-scan / holding a long write-lock / GC pause"
        // case where the short probe would spuriously orphan a live daemon.
        return await PingAsync(info, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    private static async Task<bool> PingAsync(DaemonInfo info, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var probe = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{info.Port}/"),
                Timeout = timeout,
            };
            using var response = await probe.GetAsync("status", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Return <c>true</c> when the OS reports a running process at
    /// <c>info.Pid</c> whose start time is consistent with the recorded
    /// <c>info.StartedAt</c> (i.e. not a PID-recycle false positive).
    /// </summary>
    /// <remarks>
    /// Errors from <see cref="Process.GetProcessById(int)"/> (dead pid) yield
    /// <c>false</c>. Errors reading <see cref="Process.StartTime"/> (ACL,
    /// exited-during-check) yield <c>true</c> — the conservative choice
    /// favors keeping a potentially-live daemon over evicting it.
    /// </remarks>
    private static bool IsDaemonPidAlive(DaemonInfo info)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(info.Pid);
            if (process.HasExited) return false;

            try
            {
                // A PID recycled after the daemon died will carry a StartTime
                // strictly later than the daemon.json StartedAt. Allow a small
                // grace window (1 second) for clock skew between the daemon's
                // DateTimeOffset.UtcNow and the OS process-start timestamp.
                var osStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                return osStart <= info.StartedAt + TimeSpan.FromSeconds(1);
            }
            catch (InvalidOperationException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
        catch (ArgumentException) { return false; } // no such pid
        catch (InvalidOperationException) { return false; } // process exited between checks
        finally
        {
            process?.Dispose();
        }
    }

    public void Dispose() => _http.Dispose();
}
