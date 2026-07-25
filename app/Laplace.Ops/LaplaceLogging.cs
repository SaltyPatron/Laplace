using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Laplace.Ops;

/// <summary>
/// The one shared logging foundation for every Laplace console deployable (Cli, Migrations,
/// Endpoints.Mcp, Chess.Uci, and the OpenAICompat host) — GH #602, rebuilt on Serilog
/// (GH #635 follow-up). Every path writes the CSV ops sink (read back as SQL through
/// ops.app_log); they differ only in whether a human-facing console sink rides along:
///
///   ConsoleAndFile(role) — stderr text + CSV file. For apps whose stdout is free.
///   FileOnly(role)       — CSV file only. For apps whose stdout is a wire protocol
///                          (Endpoints.Mcp JSON-RPC, Chess.Uci) — nothing can leak onto it.
///
/// role becomes the CSV application_name and the per-role file laplace-{role}.csv. The file
/// sink is a STABLE filename (Serilog shared-file append, no size roll) so the ops.app_log
/// foreign table never needs repointing; rotation is left to logrotate copytruncate, which
/// preserves the inode. Serilog owns the hard parts — cross-process shared append, buffering,
/// flush — that were hand-rolled before.
/// </summary>
public static class LaplaceLogging
{
    public static ILoggerFactory ConsoleAndFile(string role, LogEventLevel min = LogEventLevel.Information)
        => Factory(role, console: true, min);

    public static ILoggerFactory FileOnly(string role, LogEventLevel min = LogEventLevel.Information)
        => Factory(role, console: false, min);

    private static ILoggerFactory Factory(string role, bool console, LogEventLevel min)
    {
        // The console deployables put their command output on stdout, so their diagnostics
        // ride stderr (consoleToStdErr: true) — the same channel the old StderrLoggerProvider used.
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(min)
            .ApplyLaplaceSinks(role, console, consoleToStdErr: true)
            .CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }

    /// <summary>
    /// Attach the shared ops sinks to any LoggerConfiguration — used by the console factories
    /// above and by the OpenAICompat host's UseSerilog so the API writes laplace-api.csv too.
    /// The CSV file sink is always present; the console sink is optional.
    ///
    /// consoleToStdErr routes the console to STDERR — correct for the console apps, whose stdout
    /// carries command output. The web host passes FALSE (stdout): the build-time OpenAPI generator
    /// (dotnet-getdocument) runs the app and treats ANY stderr output as a build failure, so a host
    /// that logs its startup to stderr breaks `dotnet build`. stdout matches the host's prior
    /// WriteTo.Console() and is journald-captured all the same.
    /// </summary>
    public static LoggerConfiguration ApplyLaplaceSinks(
        this LoggerConfiguration config, string role, bool console, bool consoleToStdErr = true)
    {
        Directory.CreateDirectory(LaplaceInstall.OpsLogDirectory);
        var path = Path.Combine(LaplaceInstall.OpsLogDirectory, $"laplace-{role}.csv");

        config = config.Enrich.FromLogContext()
            .WriteTo.File(new OpsLogCsvTextFormatter(role), path, shared: true);

        if (console)
        {
            const string tmpl = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
            config = consoleToStdErr
                ? config.WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose, outputTemplate: tmpl)
                : config.WriteTo.Console(outputTemplate: tmpl);
        }

        return config;
    }
}
