using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Service;
using Microsoft.Extensions.Logging;

// Entry point for the detached daemon process.
//
// Accepts a single positional argument (the repository root). Additional
// switches:
//   --idle-timeout-seconds <n>   Override the 30-minute idle-exit window.
//   --app-data <dir>             Override %APPDATA%\Indexed for tests.
//
// On successful startup writes %APPDATA%\Indexed\<repoId>\daemon.json with
// the bound port and shutdown token, then blocks on the HTTP request loop
// until cancellation or an authenticated /shutdown request.

var repoRoot = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var idleSeconds = (int)TimeSpan.FromMinutes(30).TotalSeconds;
string? appData = null;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--idle-timeout-seconds":
            if (i + 1 < args.Length && int.TryParse(args[++i], out var n))
                idleSeconds = n;
            break;
        case "--app-data":
            if (i + 1 < args.Length)
                appData = args[++i];
            break;
    }
}

using var factory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
           .SetMinimumLevel(LogLevel.Information));
var logger = factory.CreateLogger<DaemonHost>();

var options = new DaemonOptions
{
    RepoRoot = repoRoot,
    AppDataBase = appData,
    IdleTimeout = TimeSpan.FromSeconds(idleSeconds),
};

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
