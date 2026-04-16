using System.IO;
using System.Text.Json;
using Indexed.Abstractions;

namespace Indexed.Cli;

/// <summary>
/// Converts daemon responses to either ripgrep-style text or raw JSON for
/// the terminal.
/// </summary>
/// <remarks>
/// <para>
/// Text form: <c>path:line:col:text</c>, one match per line. Matches agents
/// and pipelines that grew up around <c>grep</c>/<c>rg</c>. When context
/// lines are present they are emitted as <c>path-line-text</c> (ripgrep's
/// convention distinguishes matches from context with <c>:</c> vs. <c>-</c>).
/// </para>
/// <para>
/// JSON form: the raw <see cref="SearchResponse"/> wire payload with pretty
/// printing. Agents should prefer this over parsing the text form.
/// </para>
/// </remarks>
internal static class OutputFormatter
{
    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void WriteSearchText(TextWriter writer, SearchResponse response)
    {
        foreach (var match in response.Matches)
        {
            var beforeLine = match.Line - match.ContextBefore.Count;
            foreach (var ctx in match.ContextBefore)
            {
                writer.WriteLine($"{match.Path}-{beforeLine}-{ctx}");
                beforeLine++;
            }

            writer.WriteLine($"{match.Path}:{match.Line}:{match.Column}:{match.Text}");

            var afterLine = match.Line + 1;
            foreach (var ctx in match.ContextAfter)
            {
                writer.WriteLine($"{match.Path}-{afterLine}-{ctx}");
                afterLine++;
            }
        }

        if (response.Truncated)
            writer.WriteLine($"# truncated: {response.TotalMatches} total matches, caps hit");
        if (response.Freshness.IsStale)
            writer.WriteLine($"# stale: {response.Freshness.Note ?? "index not yet fresh"}");
    }

    public static void WriteSearchJson(TextWriter writer, SearchResponse response)
    {
        writer.WriteLine(JsonSerializer.Serialize(response, IndexedJsonContext.Default.SearchResponse));
    }

    public static void WriteStatusJson(TextWriter writer, StatusResponse response)
    {
        writer.WriteLine(JsonSerializer.Serialize(response, IndexedJsonContext.Default.StatusResponse));
    }

    public static void WriteStatusText(TextWriter writer, StatusResponse status)
    {
        writer.WriteLine($"daemon v{status.DaemonVersion} pid={status.Pid} schema={status.SchemaVersion}");
        writer.WriteLine($"repo    {status.RepoRoot}");
        writer.WriteLine($"repoId  {status.RepoId}");
        writer.WriteLine($"started {status.StartedAt:O}");
        writer.WriteLine(
            $"head    current={status.Freshness.CurrentHead ?? "?"}, indexed={status.Freshness.IndexedHead ?? "?"}");
        writer.WriteLine(
            $"stale   {status.Freshness.IsStale} (pending={status.Freshness.PendingFileCount})");
        if (!string.IsNullOrEmpty(status.Freshness.Note))
            writer.WriteLine($"note    {status.Freshness.Note}");
        if (status.Optimizer is { } opt)
        {
            // Operator-friendly one-liner. last= is omitted pre-first-merge
            // so a freshly-started daemon shows "merges 0" rather than a
            // misleading Unix-epoch timestamp.
            var last = opt.LastMergeAtUtc is { } at
                ? $" last={at:O} ({opt.LastMergeElapsedMs ?? 0}ms)"
                : string.Empty;
            writer.WriteLine($"merges  {opt.MergeCount}{last}");
        }
    }

    public static void WriteError(TextWriter writer, ErrorResponse error)
    {
        writer.WriteLine($"error [{error.Code}]: {error.Message}");
        if (!string.IsNullOrEmpty(error.Details))
            writer.WriteLine($"  details: {error.Details}");
    }
}
