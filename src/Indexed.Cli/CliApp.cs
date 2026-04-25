using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Service;

namespace Indexed.Cli;

/// <summary>
/// Top-level CLI dispatch: parse args, build a client, run the requested
/// verb, print results.
/// </summary>
/// <remarks>
/// <para>
/// Exit codes:
/// </para>
/// <list type="bullet">
///   <item><description><c>0</c> — success with at least one match (find) or clean status/rescan/stop.</description></item>
///   <item><description><c>1</c> — successful request but zero matches (find only).</description></item>
///   <item><description><c>2</c> — argument parse error or help shown.</description></item>
///   <item><description><c>3</c> — daemon error response (<see cref="ErrorResponse"/>).</description></item>
///   <item><description><c>4</c> — transport/launch failure.</description></item>
/// </list>
/// </remarks>
internal static class CliApp
{
    public static async Task<int> RunAsync(
        string[] rawArgs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        var args = ArgumentParser.Parse(rawArgs);
        if (args.Command == CliCommand.Help)
        {
            if (!string.IsNullOrEmpty(args.Diagnostic))
                stderr.WriteLine(args.Diagnostic);
            stdout.WriteLine(ArgumentParser.UsageText);
            return string.IsNullOrEmpty(args.Diagnostic) ? 0 : 2;
        }

        if (args.Command == CliCommand.Daemons)
            return await RunDaemonsAsync(args, stdout, stderr, cancellationToken).ConfigureAwait(false);

        try
        {
            var targetSelection = CreateTargetSelection(args);
            using var client = await DaemonClient.CreateAsync(
                targetSelection,
                appData: null,
                startupTimeout: null, // 120 s default; cold scan can take tens of seconds
                idleTimeoutSeconds: args.IdleTimeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return args.Command switch
            {
                CliCommand.Find => await RunFindAsync(client, args, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Status => await RunStatusAsync(client, args, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Rescan => await RunRescanAsync(client, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Stop => await RunStopAsync(client, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            stderr.WriteLine($"idx: {ex.Message}");
            return 4;
        }
    }

    private static TargetSelection CreateTargetSelection(CliArguments args)
    {
        if (args.Roots is { Count: > 0 })
        {
            return new TargetSelection
            {
                Roots = args.Roots,
                IndexIncludeGlobs = args.IndexIncludeGlob,
                IndexExcludeGlobs = args.IndexExcludeGlob,
                UseDefaultIndexExcludes = !args.NoDefaultExcludes,
                UseDefaultDirectoryExcludes = !args.NoDefaultDirectoryExcludes,
                MaxIndexableFileBytes = args.MaxIndexableFileBytes ?? Indexed.Targets.TargetIndexDefaults.DefaultMaxIndexableFileBytes,
            };
        }

        return new TargetSelection
        {
            RepoRoot = args.RepoRoot ?? Directory.GetCurrentDirectory(),
            IndexIncludeGlobs = args.IndexIncludeGlob,
            IndexExcludeGlobs = args.IndexExcludeGlob,
            UseDefaultIndexExcludes = !args.NoDefaultExcludes,
            UseDefaultDirectoryExcludes = false,
            MaxIndexableFileBytes = args.MaxIndexableFileBytes ?? Indexed.Targets.TargetIndexDefaults.DefaultMaxIndexableFileBytes,
        };
    }

    private static async Task<int> RunFindAsync(
        DaemonClient client, CliArguments args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var request = new SearchRequest(
            Pattern: args.Pattern!,
            Mode: args.Mode,
            IsRegex: args.IsRegex,
            CaseSensitive: args.CaseSensitive,
            KindFilter: args.KindFilter,
            PathGlob: args.PathGlob,
            ExcludeGlob: args.ExcludeGlob,
            ContextBefore: args.ContextBefore,
            ContextAfter: args.ContextAfter,
            MaxMatches: args.MaxMatches,
            MaxMatchesPerFile: args.MaxMatchesPerFile);

        var (ok, err) = await client.SearchAsync(request, ct).ConfigureAwait(false);
        if (err is not null)
        {
            OutputFormatter.WriteError(stderr, err);
            return 3;
        }
        if (ok is null)
        {
            stderr.WriteLine("idx: empty response from daemon");
            return 4;
        }

        if (args.EmitJson)
            OutputFormatter.WriteSearchJson(stdout, ok);
        else
            OutputFormatter.WriteSearchText(stdout, ok);

        return ok.Matches.Count > 0 ? 0 : 1;
    }

    private static async Task<int> RunStatusAsync(
        DaemonClient client, CliArguments args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is null)
        {
            stderr.WriteLine("idx: daemon did not return status");
            return 4;
        }

        if (args.EmitJson) OutputFormatter.WriteStatusJson(stdout, status);
        else OutputFormatter.WriteStatusText(stdout, status);
        return 0;
    }

    private static async Task<int> RunRescanAsync(
        DaemonClient client, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (await client.RescanAsync(ct).ConfigureAwait(false))
        {
            stdout.WriteLine("rescan scheduled");
            return 0;
        }
        stderr.WriteLine("idx: rescan request failed");
        return 3;
    }

    private static async Task<int> RunStopAsync(
        DaemonClient client, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (await client.ShutdownAsync(ct).ConfigureAwait(false))
        {
            stdout.WriteLine($"daemon pid={client.DaemonPid} shutting down");
            return 0;
        }
        stderr.WriteLine("idx: shutdown request failed");
        return 3;
    }

    private static Task<int> RunDaemonsAsync(
        CliArguments args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            var daemons = DaemonCatalog.List(appDataBase: null);
            if (args.EmitJson)
                OutputFormatter.WriteDaemonsJson(stdout, daemons);
            else
                OutputFormatter.WriteDaemonsText(stdout, daemons);
            return Task.FromResult(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"idx: {ex.Message}");
            return Task.FromResult(4);
        }
    }
}
