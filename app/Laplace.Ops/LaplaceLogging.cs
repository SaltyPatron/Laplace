using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Debugging;
using Serilog.Formatting.Json;
using System.Diagnostics;

namespace Laplace.Ops;

/// <summary>
/// Shared structured logging. JSONL retains complete events with daily/size rolling;
/// the six-column CSV compatibility sink is read through ops.app_log.
///
///   ConsoleAndFile(role) — stderr text + JSONL and CSV files.
///   FileOnly(role)       — JSONL and CSV files, for processes whose stdout is a wire protocol
///                          (MCP stdio JSON-RPC, UCI).
///
/// role becomes the CSV application_name and the file laplace-{role}.csv. The filename is
/// stable (Serilog shared-file append, no size roll) so the ops.app_log foreign table never
/// needs repointing. CSV retention is an operator responsibility; JSONL retains at
/// most 31 files, each rolling at 50 MiB or the next day.
/// </summary>
public static class LaplaceLogging
{
    private static readonly object TraceGate = new();
    /// <summary>Failure diagnostics from static adapters use the process-owned logger.</summary>
    public static void LogWarning(Type source, Exception exception, string message, params object?[] values)
        => Log.ForContext("SourceContext", source.FullName).Warning(exception, message, values);

    public static ILoggerProvider SharedProvider() => new SerilogLoggerProvider(Log.Logger, dispose: false);
    public static Microsoft.Extensions.Logging.ILogger CreateLogger(string category)
        => new SerilogLoggerFactory(Log.Logger, dispose: false).CreateLogger(category);

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
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .ApplyLaplaceSinks(role, console, consoleToStdErr: true, consoleMinLevel: consoleMin)
            .CreateLogger();
        Log.Logger = logger;
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
        // Serilog sink failures must reach stderr, never recursively enter a failed sink.
        SelfLog.Enable(TextWriter.Synchronized(Console.Error));
        lock (TraceGate)
            if (Trace.Listeners["LaplaceStructured"] is null) Trace.Listeners.Add(new StructuredTraceListener());
        Directory.CreateDirectory(LaplaceInstall.OpsLogDirectory);
        ShareInstallDirectory(LaplaceInstall.OpsLogDirectory);
        var path = Path.Combine(LaplaceInstall.OpsLogDirectory, $"laplace-{role}.csv");
        // File sinks otherwise report an inaccessible path only to SelfLog and can
        // leave the process running with a disabled persistent sink.
        using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }

        config = config.Enrich.FromLogContext()
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .Enrich.WithProperty("Application", role)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.WithProperty("ApplicationVersion", System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                ?? typeof(LaplaceLogging).Assembly.GetName().Version?.ToString() ?? "unknown")
            .Enrich.With(new ActivityEnricher())
            // This compatibility file has an external rotation contract. Serilog's
            // implicit 1 GiB limit must not silently stop it accepting events.
            .WriteTo.File(new OpsLogCsvTextFormatter(role), path, shared: true, fileSizeLimitBytes: null)
            .WriteTo.File(new JsonFormatter(renderMessage: true),
                Path.Combine(LaplaceInstall.OpsLogDirectory, $"laplace-{role}-.jsonl"),
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true, retainedFileCountLimit: 31, shared: true);

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
        catch (UnauthorizedAccessException ex)
        {
            SelfLog.WriteLine("Cannot apply shared log directory permissions for {0}: {1}", full, ex);
        }
    }

    private sealed class ActivityEnricher : Serilog.Core.ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory factory)
        {
            if (Activity.Current is not { } activity) return;
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("SpanId", activity.SpanId.ToHexString()));
        }
    }

    private sealed class StructuredTraceListener : TraceListener
    {
        public StructuredTraceListener() : base("LaplaceStructured") { }
        public override void Write(string? message) => Log.Information("{TraceMessage}", message);
        public override void WriteLine(string? message) => Write(message);
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
            => Log.ForContext("SourceContext", source).ForContext("EventId", id).Write(
                type is TraceEventType.Critical ? LogEventLevel.Fatal : type is TraceEventType.Error
                    ? LogEventLevel.Error : type is TraceEventType.Warning ? LogEventLevel.Warning : LogEventLevel.Information,
                "{TraceMessage}", message);
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id,
            string? format, params object?[]? args)
            => TraceEvent(cache, source, type, id, args is null ? format
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, format ?? "", args));
    }

    public static string CorrelationId(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value : Guid.NewGuid().ToString("N");
}
