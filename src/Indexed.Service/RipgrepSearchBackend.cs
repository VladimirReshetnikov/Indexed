using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Git;

namespace Indexed.Service;

/// <summary>
/// Stage 1 <see cref="ISearchBackend"/> that shells out to <c>rg</c> and
/// translates its <c>--json</c> event stream into
/// <see cref="Abstractions.Match"/> records.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the HTTP surface, CLI ergonomics, and daemon lifecycle are
/// exercised end-to-end before the real FTS5 code index lands in Stage 2.
/// Every response is marked <c>freshness.isStale = true</c> with the note
/// "ripgrep-backed; no index present yet." — callers know not to cache results.
/// </para>
/// <para>
/// Only <see cref="QueryMode.Code"/> is served. <see cref="QueryMode.Prose"/>
/// and <see cref="QueryMode.Auto"/> return a
/// <see cref="IndexedErrorCode.NotImplemented"/> response because ripgrep has
/// no prose vocabulary. The <see cref="Abstractions.Match.Kind"/> of every
/// result is <see cref="SpanKind.Code"/>; Stage 3 replaces this with true
/// extractor output.
/// </para>
/// <para>
/// Context-line support: honors <see cref="SearchRequest.ContextBefore"/> and
/// <see cref="SearchRequest.ContextAfter"/> by passing <c>-B</c>/<c>-A</c> to
/// ripgrep and collecting <c>context</c>-typed events between matches.
/// </para>
/// </remarks>
internal sealed class RipgrepSearchBackend : ISearchBackend
{
    /// <summary>Executable name resolved on <c>PATH</c>. Override in tests.</summary>
    internal static string Executable { get; set; } = "rg";

    private readonly GitRepository _repo;
    private readonly Func<Freshness> _freshnessProvider;

    public RipgrepSearchBackend(GitRepository repo, Func<Freshness> freshnessProvider)
    {
        _repo = repo;
        _freshnessProvider = freshnessProvider;
    }

    public async ValueTask<SearchBackendResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Pattern))
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.BadRequest,
                "pattern must be non-empty"));
        }

        if (request.Mode is QueryMode.Prose or QueryMode.Auto)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.NotImplemented,
                "prose and auto modes require the extractor layer (Stage 3); use mode=code for now"));
        }

        if (request.MaxMatches is < 1 or > 10_000)
            return BadRequest("maxMatches must be in [1, 10000]");
        if (request.MaxMatchesPerFile is < 1)
            return BadRequest("maxMatchesPerFile must be positive");
        if (request.ContextBefore is < 0 or > 50)
            return BadRequest("contextBefore must be in [0, 50]");
        if (request.ContextAfter is < 0 or > 50)
            return BadRequest("contextAfter must be in [0, 50]");
        if (request.TimeoutMs is < 1 or > 30_000)
            return BadRequest("timeoutMs must be in [1, 30000]");

        var sw = Stopwatch.StartNew();
        var args = BuildArgs(request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);
        var runCt = timeoutCts.Token;

        try
        {
            var (matches, truncated, total) = await RunRgAsync(args, request, runCt)
                .ConfigureAwait(false);

            sw.Stop();
            return SearchBackendResult.Ok(new SearchResponse(
                Freshness: _freshnessProvider(),
                Matches: matches,
                Truncated: truncated,
                TotalMatches: total,
                ElapsedMs: sw.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.TimeoutExceeded,
                $"search exceeded timeoutMs={request.TimeoutMs}"));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.Unavailable,
                "ripgrep (rg) not found on PATH; install ripgrep or wait for Stage 2 FTS5 index",
                ex.Message));
        }
    }

    private static SearchBackendResult BadRequest(string message)
        => SearchBackendResult.Fail(new ErrorResponse(IndexedErrorCode.BadRequest, message));

    internal static List<string> BuildArgs(SearchRequest request)
    {
        var args = new List<string>
        {
            "--json",
            "--no-messages",
            "--no-ignore-parent",
        };

        if (request.CaseSensitive) args.Add("--case-sensitive");
        else args.Add("--smart-case");

        if (!request.IsRegex) args.Add("--fixed-strings");

        if (!string.IsNullOrEmpty(request.PathGlob))
        {
            args.Add("--glob");
            args.Add(request.PathGlob);
        }

        if (request.ExcludeGlob is not null)
        {
            foreach (var ex in request.ExcludeGlob)
            {
                args.Add("--glob");
                args.Add("!" + ex);
            }
        }

        if (request.ContextBefore > 0)
        {
            args.Add("-B");
            args.Add(request.ContextBefore.ToString());
        }
        if (request.ContextAfter > 0)
        {
            args.Add("-A");
            args.Add(request.ContextAfter.ToString());
        }

        args.Add("--");
        args.Add(request.Pattern);
        args.Add(".");
        return args;
    }

    private async Task<(List<Match> matches, bool truncated, int total)> RunRgAsync(
        IReadOnlyList<string> args,
        SearchRequest request,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = _repo.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var matches = new List<Match>();
        var perFileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;
        var total = 0;
        var pending = new List<Match>();

        // Per-file state: buffered context-before lines and the most recently
        // appended match that still needs context-after attached.
        var contextBefore = new Queue<string>();
        Match? lastMatch = null;
        var lastMatchContextAfter = new List<string>();
        string? currentPath = null;

        var reader = process.StandardOutput;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var eventType = root.GetProperty("type").GetString();
            var data = root.GetProperty("data");

            switch (eventType)
            {
                case "begin":
                    FlushPendingMatch(pending, lastMatch, lastMatchContextAfter);
                    lastMatch = null;
                    lastMatchContextAfter.Clear();
                    contextBefore.Clear();
                    currentPath = GetPath(data);
                    break;

                case "end":
                    FlushPendingMatch(pending, lastMatch, lastMatchContextAfter);
                    lastMatch = null;
                    lastMatchContextAfter.Clear();
                    contextBefore.Clear();
                    currentPath = null;
                    break;

                case "context":
                    var ctxText = TrimLine(data.GetProperty("lines").GetString() ?? string.Empty);
                    if (lastMatch is not null && lastMatchContextAfter.Count < request.ContextAfter)
                    {
                        lastMatchContextAfter.Add(ctxText);
                    }
                    else
                    {
                        if (contextBefore.Count >= request.ContextBefore && request.ContextBefore > 0)
                            contextBefore.Dequeue();
                        if (request.ContextBefore > 0)
                            contextBefore.Enqueue(ctxText);
                    }
                    break;

                case "match":
                    if (currentPath is null) break;
                    total++;

                    FlushPendingMatch(pending, lastMatch, lastMatchContextAfter);
                    lastMatchContextAfter.Clear();

                    if (!perFileCounts.TryGetValue(currentPath, out var n)) n = 0;
                    if (n >= request.MaxMatchesPerFile)
                    {
                        lastMatch = null;
                        truncated = true;
                        break;
                    }
                    perFileCounts[currentPath] = n + 1;

                    var m = BuildMatch(data, currentPath, contextBefore);
                    lastMatch = m;

                    if (pending.Count + 1 >= request.MaxMatches)
                    {
                        // Reserve one slot for this match after FlushPendingMatch on next boundary.
                    }
                    if (pending.Count >= request.MaxMatches)
                    {
                        truncated = true;
                        lastMatch = null;
                    }
                    break;

                case "summary":
                    // end-of-stream; ignore summary stats for Stage 1
                    break;
            }

            if (pending.Count >= request.MaxMatches)
            {
                truncated = true;
                // Drain the process output stream, then break.
                break;
            }
        }

        FlushPendingMatch(pending, lastMatch, lastMatchContextAfter);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        // ripgrep exit codes: 0 = matches found, 1 = no matches, 2 = error.
        // Anything else with pending==0 is still a successful empty response.

        if (pending.Count > request.MaxMatches)
        {
            // Hard-cap defensively.
            pending.RemoveRange(request.MaxMatches, pending.Count - request.MaxMatches);
            truncated = true;
        }

        return (pending, truncated, total);

        static void FlushPendingMatch(List<Match> sink, Match? last, List<string> contextAfter)
        {
            if (last is null) return;
            sink.Add(last with { ContextAfter = contextAfter.ToArray() });
        }
    }

    private static string GetPath(JsonElement data)
    {
        var pathEl = data.GetProperty("path");
        // rg's --json represents paths either as {"text": "..."} (UTF-8) or
        // {"bytes": "<base64>"} when the filesystem returned non-UTF-8 bytes.
        if (pathEl.TryGetProperty("text", out var text))
            return (text.GetString() ?? string.Empty).Replace('\\', '/');
        if (pathEl.TryGetProperty("bytes", out var bytes))
        {
            var raw = Convert.FromBase64String(bytes.GetString() ?? string.Empty);
            return Encoding.UTF8.GetString(raw).Replace('\\', '/');
        }
        return string.Empty;
    }

    private static Match BuildMatch(JsonElement data, string path, Queue<string> contextBefore)
    {
        var line = data.GetProperty("line_number").GetInt32();
        var absOffset = data.GetProperty("absolute_offset").GetInt64();
        var lineText = TrimLine(
            data.GetProperty("lines").TryGetProperty("text", out var t)
                ? t.GetString() ?? string.Empty
                : string.Empty);

        // Column: first submatch start (0-based byte offset) + 1.
        var column = 1;
        if (data.TryGetProperty("submatches", out var subs) && subs.GetArrayLength() > 0)
        {
            var first = subs[0];
            if (first.TryGetProperty("start", out var startEl))
                column = startEl.GetInt32() + 1;
        }

        return new Match(
            Path: path,
            Line: line,
            Column: column,
            ByteOffset: absOffset,
            Text: lineText,
            Kind: SpanKind.Code,
            Span: null,
            ContextBefore: contextBefore.ToArray(),
            ContextAfter: Array.Empty<string>());
    }

    private static string TrimLine(string s)
    {
        // Strip a single trailing \r\n or \n without modifying interior content.
        if (s.EndsWith("\r\n", StringComparison.Ordinal))
            return s[..^2];
        if (s.EndsWith("\n", StringComparison.Ordinal))
            return s[..^1];
        return s;
    }
}
