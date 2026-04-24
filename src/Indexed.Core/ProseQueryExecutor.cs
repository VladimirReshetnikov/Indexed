using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Regex = System.Text.RegularExpressions.Regex;

namespace Indexed.Core;

/// <summary>
/// Executes prose queries against <c>prose_fts</c> and projects them to
/// <see cref="Match"/> values.
/// </summary>
public sealed class ProseQueryExecutor
{
    private const string HighlightStartMarker = "\uE000";
    private const string HighlightEndMarker = "\uE001";

    private readonly SqliteIndex _index;

    public ProseQueryExecutor(SqliteIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public async ValueTask<ProseExecuteResult> ExecuteAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var matches = new List<RankedMatch>();
        var truncated = false;
        var total = 0;

        if (request.KindFilter is { Count: > 0 } kinds && KindFilterContainsOnlyCode(kinds))
            return new ProseExecuteResult(matches, truncated, total, sw.ElapsedMilliseconds);

        var includeRegex = CompileOptionalGlob(request.PathGlob);
        var excludeFilter = new ExcludeFilter(request.ExcludeGlob);
        var perFileCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var rows = await _index
            .QueryProseCandidatesAsync(
                request.Pattern,
                HighlightStartMarker,
                HighlightEndMarker,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathAllowed(row.LogicalPath, includeRegex, excludeFilter))
                continue;
            if (request.KindFilter is { Count: > 0 } filter && !ContainsKind(filter, row.Kind))
                continue;

            total++;

            if (perFileCounts.TryGetValue(row.LogicalPath, out var fileCount)
                && fileCount >= request.MaxMatchesPerFile)
            {
                truncated = true;
                continue;
            }

            var match = BuildMatch(row, request);
            matches.Add(new RankedMatch(match, row.Rank));
            perFileCounts[row.LogicalPath] = fileCount + 1;

            if (matches.Count >= request.MaxMatches)
            {
                truncated = true;
                break;
            }
        }

        SortMatches(matches, request.SortBy);
        return new ProseExecuteResult(matches, truncated, total, sw.ElapsedMilliseconds);
    }

    private static Match BuildMatch(ProseCandidateRow row, SearchRequest request)
    {
        var content = row.Content ?? string.Empty;
        var highlighted = string.IsNullOrEmpty(row.Highlighted) ? content : row.Highlighted;
        var lineOffsets = MatchExtraction.ComputeLineOffsets(content);
        var charOffset = FindHighlightedCharOffset(highlighted);
        var (relativeLine, column) = MatchExtraction.LineAndColumnOf(charOffset, lineOffsets);
        var absoluteLine = Math.Min(row.EndLine, row.StartLine + relativeLine - 1);

        return new Match(
            Path: row.LogicalPath,
            Line: absoluteLine,
            Column: column,
            ByteOffset: MatchExtraction.Utf8ByteOffsetOf(charOffset, content),
            Text: MatchExtraction.LineTextAt(content, lineOffsets, relativeLine),
            Kind: row.Kind,
            Span: new MatchSpan(row.StartLine, row.EndLine),
            ContextBefore: MatchExtraction.ContextBefore(content, lineOffsets, relativeLine, request.ContextBefore),
            ContextAfter: MatchExtraction.ContextAfter(content, lineOffsets, relativeLine, request.ContextAfter));
    }

    private static int FindHighlightedCharOffset(string highlightedContent)
    {
        if (string.IsNullOrEmpty(highlightedContent))
            return 0;

        var plainOffset = 0;
        for (var i = 0; i < highlightedContent.Length;)
        {
            if (StartsWith(highlightedContent, i, HighlightStartMarker))
                return plainOffset;

            if (StartsWith(highlightedContent, i, HighlightEndMarker))
            {
                i += HighlightEndMarker.Length;
                continue;
            }

            plainOffset++;
            i++;
        }

        return 0;
    }

    private static bool StartsWith(string text, int index, string value)
    {
        if (index + value.Length > text.Length)
            return false;

        return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
    }

    private static void SortMatches(List<RankedMatch> matches, SortBy sortBy)
    {
        if (matches.Count < 2)
            return;

        matches.Sort(sortBy == SortBy.Relevance
            ? CompareByRelevance
            : CompareByPath);
    }

    private static int CompareByRelevance(RankedMatch left, RankedMatch right)
    {
        var rankCompare = left.Rank.CompareTo(right.Rank);
        if (rankCompare != 0)
            return rankCompare;

        return CompareByPath(left, right);
    }

    private static int CompareByPath(RankedMatch left, RankedMatch right)
    {
        var byPath = string.CompareOrdinal(left.Match.Path, right.Match.Path);
        if (byPath != 0)
            return byPath;

        var byLine = left.Match.Line.CompareTo(right.Match.Line);
        if (byLine != 0)
            return byLine;

        return left.Match.Column.CompareTo(right.Match.Column);
    }

    private static Regex? CompileOptionalGlob(string? glob)
        => string.IsNullOrEmpty(glob) ? null : PathGlob.Compile(glob);

    private static bool PathAllowed(string path, Regex? include, ExcludeFilter excludeFilter)
    {
        var normalized = path.Replace('\\', '/');
        if (include is not null && !include.IsMatch(normalized))
            return false;
        if (excludeFilter.IsExcluded(path))
            return false;

        return true;
    }

    private static bool KindFilterContainsOnlyCode(IReadOnlyList<SpanKind> filter)
    {
        for (var i = 0; i < filter.Count; i++)
        {
            if (filter[i] != SpanKind.Code)
                return false;
        }

        return true;
    }

    private static bool ContainsKind(IReadOnlyList<SpanKind> filter, SpanKind kind)
    {
        for (var i = 0; i < filter.Count; i++)
        {
            if (filter[i] == kind)
                return true;
        }

        return false;
    }
}

public sealed record RankedMatch(Match Match, double Rank);

public sealed record ProseExecuteResult(
    IReadOnlyList<RankedMatch> Matches,
    bool Truncated,
    int ReportedTotal,
    long ElapsedMs);
