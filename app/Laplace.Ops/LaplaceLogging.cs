using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Laplace.Ops;

/// <summary>
/// Logging shared by every Laplace process. Every path writes the CSV ops sink, which SQL
/// reads back through ops.app_log; they differ only in whether a console sink is added:
///
///   ConsoleAndFile(role) — stderr text + CSV file.
///   FileOnly(role)       — CSV file only, for processes whose stdout is a wire protocol
///                          (MCP stdio JSON-RPC, UCI).
///
/// role becomes the CSV application_name and the file laplace-{role}.csv. The filename is
/// stable (Serilog shared-file append, no size roll) so the ops.app_log foreign table never
/// needs repointing; rotation is external (logrotate copytruncate keeps the inode).
/// </summary>
public static class LaplaceLogging
{
    public static ILoggerFactory ConsoleAndFile(string role, LogEventLevel min = LogEventLevel.Information)
        => Factory(role, console: true, min);

    public static ILoggerFactory FileOnly(string role, LogEventLevel min = LogEventLevel.Information)
        => Factory(role, console: false, min);

    private static ILoggerFactory Factory(string role, bool console, LogEventLevel min)
    {
        // The CSV file keeps the full minimum level; LAPLACE_INGEST_CONSOLE=ci|quiet (or
        // GITHUB_ACTIONS with it unset) raises only the console sink to Warning.
        LogEventLevel consoleMin = min;
        string? mode = Environment.GetEnvironmentVariable("LAPLACE_INGEST_CONSOLE");
        bool ci = string.Equals(mode, "ci", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "quiet", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
                && string.IsNullOrEmpty(mode));
        if (ci && console) consoleMin = LogEventLevel.Warning;

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(min)
            .ApplyLaplaceSinks(role, console, consoleToStdErr: true, consoleMinLevel: consoleMin)
            .CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }

    /// <summary>
    /// Attach the shared ops sinks to any LoggerConfiguration (the console factories above and
    /// the OpenAI-compatible host's UseSerilog). The CSV file sink is always present; the
    /// console sink is optional.
    ///
    /// consoleToStdErr sends console output to stderr, keeping stdout for command output. The
    /// web host passes false because the build-time OpenAPI generator runs the app and treats
    /// any stderr output as a build failure.
    /// </summary>
    public static LoggerConfiguration ApplyLaplaceSinks(
        this LoggerConfiguration config, string role, bool console, bool consoleToStdErr = true,
        LogEventLevel consoleMinLevel = LogEventLevel.Verbose)
    {
        Directory.CreateDirectory(LaplaceInstall.OpsLogDirectory);
        ShareInstallDirectory(LaplaceInstall.OpsLogDirectory);
        var path = Path.Combine(LaplaceInstall.OpsLogDirectory, $"laplace-{role}.csv");

        config = config.Enrich.FromLogContext()
            .WriteTo.File(new OpsLogCsvTextFormatter(role), path, shared: true);

        if (console)
        {
            const string tmpl = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
            config = consoleToStdErr
                ? config.WriteTo.Console(
                    restrictedToMinimumLevel: consoleMinLevel,
                    standardErrorFromLevel: LogEventLevel.Verbose,
                    outputTemplate: tmpl)
                : config.WriteTo.Console(
                    restrictedToMinimumLevel: consoleMinLevel,
                    outputTemplate: tmpl);
        }

        return config;
    }

    /// <summary>
    /// Adds g+rwxs to a directory under the install root, since umask 022 leaves a
    /// directory created under a setgid parent without group write for the other writer.
    /// </summary>
    internal static void ShareInstallDirectory(string path, string installRoot = "/opt/laplace")
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(installRoot);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) && full != root) return;
        try
        {
            var info = new DirectoryInfo(full);
            info.UnixFileMode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
