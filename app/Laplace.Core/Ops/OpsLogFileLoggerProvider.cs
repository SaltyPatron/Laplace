using System.Text;
using Microsoft.Extensions.Logging;

namespace Laplace.Engine.Core.Ops;

/// <summary>
/// Writes diagnostics as RFC 4180 CSV to a STABLE per-role file under
/// <see cref="LaplaceInstall.OpsLogDirectory"/> — <c>laplace-{role}.csv</c> — so a
/// file_fdw foreign table (ops.app_log) never needs repointing. Cross-process safe:
/// each line is a single append to a file opened <see cref="FileMode.Append"/>
/// (O_APPEND on Linux → atomic for the sub-PIPE_BUF lines we write) with
/// <see cref="FileShare.ReadWrite"/>, so the CLI, the API, and any number of
/// concurrent UCI engines can share the file without a coordinator.
///
/// Diagnostic logging is low-volume by construction (logging inside the per-row /
/// per-batch ingest loops is banned — GH #601), so open-append-close per line is well
/// within budget and keeps the writer trivially correct.
/// </summary>
public sealed class OpsLogFileLoggerProvider : ILoggerProvider
{
    private readonly string _role;
    private readonly string _path;
    private readonly LogLevel _min;
    private readonly long _rollBytes;
    private readonly object _gate = new();

    public OpsLogFileLoggerProvider(string role, LogLevel min = LogLevel.Information,
                                    long rollBytes = 64L * 1024 * 1024)
    {
        _role = role;
        _min = min;
        _rollBytes = rollBytes;
        Directory.CreateDirectory(LaplaceInstall.OpsLogDirectory);
        _path = Path.Combine(LaplaceInstall.OpsLogDirectory, $"laplace-{role}.csv");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        string severity = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "LOG",
        };
        string? detail = ex is null ? null : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        string line = OpsLogCsvFormatter.Format(
            DateTimeOffset.Now, _role, severity, category, message, detail);
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                using var fs = new FileStream(
                    _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            catch (IOException)
            {
                // A diagnostic sink must never take down the process it is observing.
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _rollBytes) return;
        // Timestamped archive so the current name stays stable. Best-effort: if another
        // process already rolled, the source is gone and Move throws — harmless.
        string archive = Path.Combine(
            LaplaceInstall.OpsLogDirectory,
            $"laplace-{_role}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv");
        try { File.Move(_path, archive); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly OpsLogFileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(OpsLogFileLoggerProvider owner, string category)
        {
            _owner = owner;
            int colon = category.LastIndexOf('.');
            _category = colon >= 0 ? category[(colon + 1)..] : category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel level) => level >= _owner._min && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            _owner.Write(level, _category, formatter(state, ex), ex);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
