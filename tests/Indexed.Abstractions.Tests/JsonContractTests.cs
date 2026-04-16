using System;
using System.Collections.Generic;
using System.Text.Json;
using Indexed.Abstractions;
using Xunit;

namespace Indexed.Abstractions.Tests;

/// <summary>
/// Contract tests for every DTO exchanged over the Indexed HTTP surface.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the wire format. Any rename, casing change, or enum
/// renumbering breaks at least one assertion here before it can reach
/// consumers. Round-trip tests guarantee structural fidelity; explicit
/// string assertions guarantee the kebab-case wire vocabulary stays stable.
/// </para>
/// </remarks>
public sealed class JsonContractTests
{
    private static readonly IndexedJsonContext Context = IndexedJsonContext.Default;

    // ----- SearchRequest -----

    [Fact]
    public void SearchRequest_MinimalPayload_DeserializesWithDefaults()
    {
        // The CLI and agents may send {"pattern": "..."} and expect every
        // other field to fall back to its documented default. Lock the
        // defaults here so a later refactor cannot silently shift them.
        const string json = """{"pattern":"foo"}""";

        var request = JsonSerializer.Deserialize(json, Context.SearchRequest)!;

        Assert.Equal("foo", request.Pattern);
        Assert.Equal(QueryMode.Auto, request.Mode);
        Assert.False(request.IsRegex);
        Assert.False(request.CaseSensitive);
        Assert.Null(request.KindFilter);
        Assert.Null(request.PathGlob);
        Assert.Null(request.ExcludeGlob);
        Assert.Equal(0, request.ContextBefore);
        Assert.Equal(0, request.ContextAfter);
        Assert.Equal(200, request.MaxMatches);
        Assert.Equal(20, request.MaxMatchesPerFile);
        Assert.Equal(SortBy.Path, request.SortBy);
        Assert.Equal(2000, request.TimeoutMs);
    }

    [Fact]
    public void SearchRequest_FullyPopulated_RoundTrips()
    {
        var original = new SearchRequest(
            Pattern: "class\\s+\\w+Index",
            Mode: QueryMode.Code,
            IsRegex: true,
            CaseSensitive: true,
            KindFilter: new[] { SpanKind.Code, SpanKind.XmlDoc },
            PathGlob: "src/**/*.cs",
            ExcludeGlob: new[] { "**/bin/**", "**/obj/**" },
            ContextBefore: 2,
            ContextAfter: 3,
            MaxMatches: 500,
            MaxMatchesPerFile: 25,
            SortBy: SortBy.Relevance,
            TimeoutMs: 5000);

        // Record equality doesn't do deep collection comparison, so a plain
        // Assert.Equal on the records after deserialization fails even when
        // the payload is identical. Compare the canonical JSON before/after a
        // second serialization instead — this pins both the shape and the
        // field values.
        AssertJsonRoundTrip(original, Context.SearchRequest);
    }

    [Fact]
    public void SearchRequest_SerializesUsingCamelCaseKeys()
    {
        var request = new SearchRequest("pat")
        {
            CaseSensitive = true,
            PathGlob = "x",
            MaxMatches = 7,
            MaxMatchesPerFile = 3,
        };

        var json = JsonSerializer.Serialize(request, Context.SearchRequest);

        Assert.Contains("\"pattern\":", json);
        Assert.Contains("\"caseSensitive\":true", json);
        Assert.Contains("\"pathGlob\":\"x\"", json);
        Assert.Contains("\"maxMatches\":7", json);
        Assert.Contains("\"maxMatchesPerFile\":3", json);
    }

    // ----- QueryMode -----

    [Theory]
    [InlineData(QueryMode.Auto, "\"auto\"")]
    [InlineData(QueryMode.Code, "\"code\"")]
    [InlineData(QueryMode.Prose, "\"prose\"")]
    public void QueryMode_WireStrings_AreKebabCase(QueryMode value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value, Context.QueryMode);
        Assert.Equal(expectedJson, json);

        var round = JsonSerializer.Deserialize(expectedJson, Context.QueryMode);
        Assert.Equal(value, round);
    }

    [Fact]
    public void QueryMode_UnknownString_Throws()
    {
        // Unknown wire values must not be silently coerced; the daemon
        // depends on invalid requests producing a BadRequest response.
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("\"lexer\"", Context.QueryMode));
    }

    // ----- SpanKind -----

    [Theory]
    [InlineData(SpanKind.Code, "\"code\"")]
    [InlineData(SpanKind.Markdown, "\"markdown\"")]
    [InlineData(SpanKind.PlainText, "\"plain-text\"")]
    [InlineData(SpanKind.XmlDoc, "\"xml-doc\"")]
    [InlineData(SpanKind.LineCommentBlock, "\"line-comment-block\"")]
    [InlineData(SpanKind.BlockComment, "\"block-comment\"")]
    public void SpanKind_WireStrings_AreKebabCase(SpanKind value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value, Context.SpanKind);
        Assert.Equal(expectedJson, json);

        var round = JsonSerializer.Deserialize(expectedJson, Context.SpanKind);
        Assert.Equal(value, round);
    }

    // ----- SortBy -----

    [Theory]
    [InlineData(SortBy.Path, "\"path\"")]
    [InlineData(SortBy.Relevance, "\"relevance\"")]
    public void SortBy_WireStrings_AreKebabCase(SortBy value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value, Context.SortBy);
        Assert.Equal(expectedJson, json);
    }

    // ----- Match / MatchSpan -----

    [Fact]
    public void Match_CodeHit_OmitsNullSpan()
    {
        // Code matches carry a null Span. The source-gen context is configured
        // with DefaultIgnoreCondition = WhenWritingNull, so the wire output
        // must not contain a "span" key at all for code hits.
        var match = new Match(
            Path: "src/foo.cs",
            Line: 42,
            Column: 8,
            ByteOffset: 1024,
            Text: "    var x = 1;",
            Kind: SpanKind.Code,
            Span: null,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());

        var json = JsonSerializer.Serialize(match, Context.Match);

        Assert.DoesNotContain("\"span\"", json);
        Assert.Contains("\"kind\":\"code\"", json);
        Assert.Contains("\"line\":42", json);
        Assert.Contains("\"byteOffset\":1024", json);
    }

    [Fact]
    public void Match_ProseHit_IncludesSpan()
    {
        var match = new Match(
            Path: "README.md",
            Line: 3,
            Column: 1,
            ByteOffset: 42,
            Text: "# Indexed",
            Kind: SpanKind.Markdown,
            Span: new MatchSpan(1, 10),
            ContextBefore: new[] { "" },
            ContextAfter: new[] { "" });

        var json = JsonSerializer.Serialize(match, Context.Match);

        Assert.Contains("\"span\":{\"startLine\":1,\"endLine\":10}", json);
        AssertJsonRoundTrip(match, Context.Match);
    }

    // ----- Freshness -----

    [Fact]
    public void Freshness_StaleRipgrepShape_RoundTrips()
    {
        var fresh = new Freshness(
            IndexedHead: null,
            CurrentHead: "9d348b9f",
            PendingFileCount: 0,
            LastFullScanAt: null,
            IsStale: true,
            Note: "ripgrep-backed; no index present yet.");

        var json = JsonSerializer.Serialize(fresh, Context.Freshness);
        var round = JsonSerializer.Deserialize(json, Context.Freshness);

        Assert.Equal(fresh, round);
        Assert.DoesNotContain("\"indexedHead\":", json); // null-ignored
        Assert.DoesNotContain("\"lastFullScanAt\":", json);
        Assert.Contains("\"isStale\":true", json);
    }

    [Fact]
    public void Freshness_IndexedShape_SerializesUtcTimestamp()
    {
        var ts = new DateTimeOffset(2026, 4, 15, 3, 10, 12, TimeSpan.Zero);
        var fresh = new Freshness(
            IndexedHead: "9d348b9f",
            CurrentHead: "9d348b9f",
            PendingFileCount: 0,
            LastFullScanAt: ts,
            IsStale: false);

        var json = JsonSerializer.Serialize(fresh, Context.Freshness);

        Assert.Contains("\"lastFullScanAt\":\"2026-04-15T03:10:12+00:00\"", json);
        Assert.Contains("\"isStale\":false", json);
    }

    // ----- SearchResponse -----

    [Fact]
    public void SearchResponse_RoundTripsWithMatches()
    {
        var response = new SearchResponse(
            Freshness: new Freshness(null, "abc", 0, null, true, "ripgrep-backed"),
            Matches: new[]
            {
                new Match(
                    Path: "a.cs",
                    Line: 1,
                    Column: 1,
                    ByteOffset: 0,
                    Text: "class A {}",
                    Kind: SpanKind.Code,
                    Span: null,
                    ContextBefore: Array.Empty<string>(),
                    ContextAfter: Array.Empty<string>()),
            },
            Truncated: false,
            TotalMatches: 1,
            ElapsedMs: 4);

        AssertJsonRoundTrip(response, Context.SearchResponse);
    }

    [Fact]
    public void SearchResponse_EmptyMatches_SerializesAsEmptyArray()
    {
        var response = new SearchResponse(
            Freshness: new Freshness(null, "abc", 0, null, true),
            Matches: Array.Empty<Match>(),
            Truncated: false,
            TotalMatches: 0,
            ElapsedMs: 1);

        var json = JsonSerializer.Serialize(response, Context.SearchResponse);

        Assert.Contains("\"matches\":[]", json);
    }

    // ----- StatusResponse -----

    [Fact]
    public void StatusResponse_RoundTrips()
    {
        var status = new StatusResponse(
            DaemonVersion: "0.1.0-s1",
            SchemaVersion: 0,
            Pid: 12345,
            RepoRoot: @"C:\Tools2\Tools",
            RepoId: "9d348b9ff1a2",
            StartedAt: new DateTimeOffset(2026, 4, 15, 3, 0, 0, TimeSpan.Zero),
            Freshness: new Freshness(null, "abc", 0, null, true, "ripgrep-backed"));

        var json = JsonSerializer.Serialize(status, Context.StatusResponse);
        var round = JsonSerializer.Deserialize(json, Context.StatusResponse);

        Assert.Equal(status, round);
    }

    [Fact]
    public void StatusResponse_NullOptimizer_OmitsKey()
    {
        // The Optimizer field is the new /status projection of IndexOptimizer
        // counters. When the daemon was started without an optimizer (test
        // backend override or explicit disable), the field must not appear
        // on the wire — old clients continue to see the response shape they
        // expected before N4 landed.
        var status = new StatusResponse(
            DaemonVersion: "0.1.0-s2",
            SchemaVersion: 2,
            Pid: 7,
            RepoRoot: @"C:\tmp",
            RepoId: "a1b2c3d4e5f6",
            StartedAt: DateTimeOffset.UnixEpoch,
            Freshness: new Freshness("abc", "abc", 0, null, false),
            Optimizer: null);

        var json = JsonSerializer.Serialize(status, Context.StatusResponse);

        Assert.DoesNotContain("\"optimizer\":", json);
    }

    [Fact]
    public void StatusResponse_WithOptimizer_RoundTripsStats()
    {
        var status = new StatusResponse(
            DaemonVersion: "0.1.0-s2",
            SchemaVersion: 2,
            Pid: 7,
            RepoRoot: @"C:\tmp",
            RepoId: "a1b2c3d4e5f6",
            StartedAt: DateTimeOffset.UnixEpoch,
            Freshness: new Freshness("abc", "abc", 0, null, false),
            Optimizer: new OptimizerStats(
                MergeCount: 42,
                LastMergeAtUtc: new DateTimeOffset(2026, 4, 15, 3, 10, 12, TimeSpan.Zero),
                LastMergeElapsedMs: 17));

        var json = JsonSerializer.Serialize(status, Context.StatusResponse);
        var round = JsonSerializer.Deserialize(json, Context.StatusResponse)!;

        Assert.Equal(status, round);
        Assert.Contains("\"mergeCount\":42", json);
        Assert.Contains("\"lastMergeElapsedMs\":17", json);
    }

    [Fact]
    public void OptimizerStats_ZeroState_OmitsNullTimestampAndElapsed()
    {
        // Immediately after daemon start — MergeCount = 0, the two nullable
        // fields are null. Both should be absent from the wire payload to
        // match the DefaultIgnoreCondition policy.
        var stats = new OptimizerStats(
            MergeCount: 0,
            LastMergeAtUtc: null,
            LastMergeElapsedMs: null);

        var json = JsonSerializer.Serialize(stats, Context.OptimizerStats);

        Assert.Contains("\"mergeCount\":0", json);
        Assert.DoesNotContain("\"lastMergeAtUtc\":", json);
        Assert.DoesNotContain("\"lastMergeElapsedMs\":", json);
    }

    // ----- ErrorResponse / IndexedErrorCode -----

    [Theory]
    [InlineData(IndexedErrorCode.BadRequest, "\"bad-request\"")]
    [InlineData(IndexedErrorCode.PatternInvalid, "\"pattern-invalid\"")]
    [InlineData(IndexedErrorCode.TimeoutExceeded, "\"timeout-exceeded\"")]
    [InlineData(IndexedErrorCode.RepoNotFound, "\"repo-not-found\"")]
    [InlineData(IndexedErrorCode.Unavailable, "\"unavailable\"")]
    [InlineData(IndexedErrorCode.NotImplemented, "\"not-implemented\"")]
    [InlineData(IndexedErrorCode.Internal, "\"internal\"")]
    [InlineData(IndexedErrorCode.Forbidden, "\"forbidden\"")]
    public void IndexedErrorCode_WireStrings_AreKebabCase(IndexedErrorCode code, string expectedJson)
    {
        var json = JsonSerializer.Serialize(code, Context.IndexedErrorCode);
        Assert.Equal(expectedJson, json);

        var round = JsonSerializer.Deserialize(expectedJson, Context.IndexedErrorCode);
        Assert.Equal(code, round);
    }

    [Fact]
    public void ErrorResponse_RoundTrips()
    {
        var err = new ErrorResponse(
            Code: IndexedErrorCode.BadRequest,
            Message: "pattern must be non-empty",
            Details: "field: pattern");

        var json = JsonSerializer.Serialize(err, Context.ErrorResponse);
        var round = JsonSerializer.Deserialize(json, Context.ErrorResponse);

        Assert.Equal(err, round);
        Assert.Contains("\"code\":\"bad-request\"", json);
    }

    [Fact]
    public void ErrorResponse_WithoutDetails_OmitsKey()
    {
        var err = new ErrorResponse(
            Code: IndexedErrorCode.NotImplemented,
            Message: "prose mode lands in Stage 3");

        var json = JsonSerializer.Serialize(err, Context.ErrorResponse);

        Assert.DoesNotContain("\"details\":", json);
    }

    // ----- Case-insensitive property matching -----

    [Fact]
    public void DeserializerAcceptsMixedCasePropertyNames()
    {
        // PropertyNameCaseInsensitive = true on the context. Hand-authored
        // requests from agents should not fail over casing differences.
        const string json = """{"Pattern":"foo","Mode":"code","CaseSensitive":true}""";

        var request = JsonSerializer.Deserialize(json, Context.SearchRequest)!;

        Assert.Equal("foo", request.Pattern);
        Assert.Equal(QueryMode.Code, request.Mode);
        Assert.True(request.CaseSensitive);
    }

    // Round-trips a DTO via JSON and asserts the second serialization produces
    // the same payload. Replaces direct record equality for types holding
    // IReadOnlyList fields, which record-synthesized Equals does not deep-compare.
    private static void AssertJsonRoundTrip<T>(
        T original,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var first = JsonSerializer.Serialize(original, typeInfo);
        var round = JsonSerializer.Deserialize(first, typeInfo)!;
        var second = JsonSerializer.Serialize(round, typeInfo);
        Assert.Equal(first, second);
    }
}
