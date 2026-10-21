using System.Globalization;
using DbUp.Engine.Output;
using Laplace.Engine.Core;
using Serilog.Events;

namespace Laplace.Migrations;

/// <summary>
/// DbUp's ConsoleUpgradeLog treats every message as a composite-format string even when
/// no formatting arguments were supplied. dbup-postgresql logs PostgresException.ToString()
/// through that zero-argument path; PostgreSQL diagnostics can legitimately contain '{'
/// and '}', so the logger can throw FormatException and replace the database error that
/// migration is supposed to report. This sink preserves zero-argument messages literally
/// and still supports DbUp's normal indexed format templates when arguments are present.
/// </summary>
internal sealed class LiteralSafeUpgradeLog : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => Write("TRACE", Console.Out, format, args);
    public void LogDebug(string format, params object[] args) => Write("DEBUG", Console.Out, format, args);
    public void LogInformation(string format, params object[] args) => Write("INFO", Console.Out, format, args);
    public void LogWarning(string format, params object[] args) => Write("WARN", Console.Error, format, args);
    public void LogError(string format, params object[] args) => Write("ERROR", Console.Error, format, args);

    public void LogError(Exception ex, string format, params object[] args)
    {
        var message = SafeMessage(Render(format, args));
        Serilog.Log.ForContext<LiteralSafeUpgradeLog>().Error(ex, "Migration failed: {MigrationMessage}", message);
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(ex);
    }

    internal static string Render(string format, object[] args)
    {
        if (args.Length == 0)
            return format;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // Logging never replaces the failure it describes: on a format error the
            // template and every argument value are written as-is.
            var renderedArgs = string.Join(", ", args.Select(static value => value?.ToString() ?? "<null>"));
            return $"{format} [format-arguments: {renderedArgs}]";
        }
    }

    private static void Write(string level, TextWriter writer, string format, object[] args)
    {
        var message = SafeMessage(Render(format, args));
        var severity = level switch
        {
            "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };
        Serilog.Log.ForContext<LiteralSafeUpgradeLog>().Write(severity, "{MigrationMessage}", message);
        writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
    }

    private static string SafeMessage(string message)
    {
        const string marker = "ConnectionString => ";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return start < 0 ? message : message[..(start + marker.Length)]
            + LaplaceInstall.RedactConnectionString(message[(start + marker.Length)..]);
    }
}
