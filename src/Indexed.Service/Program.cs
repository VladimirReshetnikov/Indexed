using System;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Service;
using Microsoft.Extensions.Logging;

// Entry point for the detached daemon process.
//
// Legacy form:
//   Indexed.Service.exe <repo-root>
//
// Current switches:
//   --repo-root <dir>                   Git compatibility target
//   --root <dir>                        Single directory-tree target
//   --root <label=dir> [--root ...]     Multi-root directory-set target
//   --idle-timeout-seconds <n>   Override the 30-minute idle-exit window.
//   --app-data <dir>             Override %LOCALAPPDATA%\Indexed for tests.
//   --exclude-index <glob>       Repeatable extra index-time excludes.
//   --no-default-excludes        Disable the binary-adjacent default excludes.
//   --no-default-directory-excludes
//                                Disable the directory-mode default excludes.
//
// On successful startup writes %LOCALAPPDATA%\Indexed\<targetId>\daemon.json
// with the bound port and shutdown token, then blocks on the HTTP request
// loop until cancellation or an authenticated /shutdown request.

var options = DaemonCommandLine.ParseOptions(args);

using var factory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
           .SetMinimumLevel(LogLevel.Information));
var logger = factory.CreateLogger<DaemonHost>();

await using var host = new DaemonHost(options, logger);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    host.RequestShutdown();
};

try
{
    await host.StartAsync(cts.Token).ConfigureAwait(false);
    logger.LogInformation("daemon started; listening on port {Port}", host.Info.Port);
    await host.RunAsync(cts.Token).ConfigureAwait(false);
}
catch (DaemonAlreadyRunningException ex)
{
    logger.LogWarning(ex, "daemon already running; exiting");
    return 1;
}
catch (Exception ex)
{
    logger.LogError(ex, "daemon terminated abnormally");
    return 2;
}

return 0;
