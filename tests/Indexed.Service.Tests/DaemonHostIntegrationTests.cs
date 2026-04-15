using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Git;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

/// <summary>
/// In-process integration tests: spin up a real <see cref="DaemonHost"/> on
/// an ephemeral loopback port with a fake backend, then exercise the HTTP
/// surface via <see cref="HttpClient"/>.
/// </summary>
public sealed class DaemonHostIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public DaemonHostIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IndexedHost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private string NewRepo()
    {
        var path = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(path);
        GitProcess.RunText(path, new[] { "init", "-q", "-b", "main" });
        GitProcess.RunText(path, new[] { "config", "user.name", "t" });
        GitProcess.RunText(path, new[] { "config", "user.email", "t@t" });
        GitProcess.RunText(path, new[] { "config", "commit.gpgsign", "false" });
        File.WriteAllText(Path.Combine(path, "a.txt"), "seed");
        GitProcess.RunText(path, new[] { "add", "a.txt" });
        GitProcess.RunText(path, new[] { "commit", "-q", "-m", "init" });
        return path;
    }

    private async Task<(DaemonHost host, HttpClient http)> StartHostAsync(ISearchBackend backend)
    {
        var repo = NewRepo();
        var appData = Path.Combine(_tempRoot, "appdata");
        Directory.CreateDirectory(appData);

        var host = new DaemonHost(new DaemonOptions
        {
            RepoRoot = repo,
            AppDataBase = appData,
            UseSingletonMutex = false,
            BackendOverride = backend,
            IdleTimeout = TimeSpan.FromMinutes(5),
        });

        await host.StartAsync();
        _ = host.RunAsync();

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Info.Port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        return (host, client);
    }

    private sealed class FakeBackend : ISearchBackend
    {
        private readonly SearchBackendResult _result;
        public FakeBackend(SearchBackendResult result) { _result = result; }
        public ValueTask<SearchBackendResult> SearchAsync(
            SearchRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(_result);
    }

    // ----- tests -----

    [Fact]
    public async Task Status_ReturnsExpectedShape()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(
            SearchBackendResult.Ok(new SearchResponse(
                new Freshness(null, "x", 0, null, true, "fake"),
                Array.Empty<Match>(), false, 0, 0))));
        try
        {
            var status = await http.GetFromJsonAsync(
                "status", IndexedJsonContext.Default.StatusResponse);

            Assert.NotNull(status);
            Assert.True(status!.Freshness.IsStale);
            Assert.Equal(12, status.RepoId.Length);
            Assert.Equal(host.Info.Pid, status.Pid);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DaemonJsonWrittenOnStart()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(
            SearchBackendResult.Ok(new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        try
        {
            var appData = Path.Combine(_tempRoot, "appdata", host.Info.RepoId);
            var daemonJson = Path.Combine(appData, "daemon.json");

            Assert.True(File.Exists(daemonJson));
            var info = DaemonInfo.TryRead(daemonJson);
            Assert.NotNull(info);
            Assert.Equal(host.Info.Port, info!.Port);
            Assert.Equal(host.Info.ShutdownToken, info.ShutdownToken);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_ReturnsResponseFromBackend()
    {
        var fake = new SearchResponse(
            new Freshness(null, "abc", 0, null, true, "fake"),
            new[]
            {
                new Match(
                    Path: "a.txt", Line: 1, Column: 1, ByteOffset: 0,
                    Text: "seed", Kind: SpanKind.Code, Span: null,
                    ContextBefore: Array.Empty<string>(),
                    ContextAfter: Array.Empty<string>()),
            },
            Truncated: false, TotalMatches: 1, ElapsedMs: 1);

        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(fake)));
        try
        {
            using var resp = await http.PostAsJsonAsync(
                "search",
                new SearchRequest("seed", Mode: QueryMode.Code),
                IndexedJsonContext.Default.SearchRequest);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var body = JsonSerializer.Deserialize(stream, IndexedJsonContext.Default.SearchResponse);
            Assert.NotNull(body);
            Assert.Single(body!.Matches);
            Assert.Equal("a.txt", body.Matches[0].Path);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_SurfacesBackendErrorShape()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(
            SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.NotImplemented, "prose not implemented"))));
        try
        {
            using var resp = await http.PostAsJsonAsync(
                "search",
                new SearchRequest("x", Mode: QueryMode.Prose),
                IndexedJsonContext.Default.SearchRequest);
            Assert.Equal(501, (int)resp.StatusCode);

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var err = JsonSerializer.Deserialize(stream, IndexedJsonContext.Default.ErrorResponse);
            Assert.Equal(IndexedErrorCode.NotImplemented, err!.Code);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_MalformedJson_ReturnsBadRequest()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(
            new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        try
        {
            using var content = new StringContent(
                "{not json", System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("search", content);
            Assert.Equal(400, (int)resp.StatusCode);

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var err = JsonSerializer.Deserialize(stream, IndexedJsonContext.Default.ErrorResponse);
            Assert.Equal(IndexedErrorCode.BadRequest, err!.Code);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_RejectsWithoutToken()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(
            new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        try
        {
            using var resp = await http.PostAsync("shutdown", content: null);
            Assert.Equal(403, (int)resp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_AcceptsWithMatchingToken()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(
            new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "shutdown");
            req.Headers.Add("X-Indexed-Shutdown-Token", host.Info.ShutdownToken);

            using var resp = await http.SendAsync(req);
            Assert.Equal(204, (int)resp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(
            new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        try
        {
            using var resp = await http.GetAsync("nope");
            Assert.Equal(404, (int)resp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DefaultBackend_AnswersCodeSearchFromSqliteIndex()
    {
        // No BackendOverride → DaemonHost opens index.db, runs a full scan on
        // the seeded repo, and the SqliteSearchBackend serves the query.
        var repo = NewRepoWithContent(new[]
        {
            ("hello.cs", "class Hello { void M() { var needle = 42; } }"),
            ("other.cs", "class Other { }"),
        });
        var appData = Path.Combine(_tempRoot, "appdata-real");
        Directory.CreateDirectory(appData);

        var host = new DaemonHost(new DaemonOptions
        {
            RepoRoot = repo,
            AppDataBase = appData,
            UseSingletonMutex = false,
            IdleTimeout = TimeSpan.FromMinutes(5),
        });

        try
        {
            await host.StartAsync();
            _ = host.RunAsync();

            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{host.Info.Port}/"),
                Timeout = TimeSpan.FromSeconds(10),
            };

            using var resp = await http.PostAsJsonAsync(
                "search",
                new SearchRequest("needle", Mode: QueryMode.Code),
                IndexedJsonContext.Default.SearchRequest);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var body = JsonSerializer.Deserialize(stream, IndexedJsonContext.Default.SearchResponse);
            Assert.NotNull(body);
            var match = Assert.Single(body!.Matches);
            Assert.Equal("hello.cs", match.Path);
            Assert.Contains("needle", match.Text);

            // Status reflects real schema version and fresh index.
            var status = await http.GetFromJsonAsync(
                "status", IndexedJsonContext.Default.StatusResponse);
            Assert.NotNull(status);
            Assert.Equal(1, status!.SchemaVersion);
            Assert.False(status.Freshness.IsStale);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private string NewRepoWithContent((string path, string content)[] files)
    {
        var path = Path.Combine(_tempRoot, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        GitProcess.RunText(path, new[] { "init", "-q", "-b", "main" });
        GitProcess.RunText(path, new[] { "config", "user.name", "t" });
        GitProcess.RunText(path, new[] { "config", "user.email", "t@t" });
        GitProcess.RunText(path, new[] { "config", "commit.gpgsign", "false" });
        foreach (var (relPath, content) in files)
        {
            var full = Path.Combine(path, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        GitProcess.RunText(path, new[] { "add", "-A" });
        GitProcess.RunText(path, new[] { "commit", "-q", "-m", "init" });
        return path;
    }

    [Fact]
    public async Task DisposeDeletesDaemonJson()
    {
        var (host, http) = await StartHostAsync(new FakeBackend(SearchBackendResult.Ok(
            new SearchResponse(
                new Freshness(null, null, 0, null, true), Array.Empty<Match>(), false, 0, 0))));
        var daemonJson = Path.Combine(_tempRoot, "appdata", host.Info.RepoId, "daemon.json");
        Assert.True(File.Exists(daemonJson));

        http.Dispose();
        await host.DisposeAsync();

        Assert.False(File.Exists(daemonJson));
    }
}
