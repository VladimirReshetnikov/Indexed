using System;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Cli;

// Entry point for `idx`. Delegates to CliApp.RunAsync so the dispatch is
// testable without spawning a real process.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await CliApp.RunAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 130; // SIGINT convention
}
