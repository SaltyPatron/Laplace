using Microsoft.Extensions.Logging;

namespace Laplace.Engine.Core.Ops;

/// <summary>
/// Plain-text diagnostics to <see cref="Console.Error"/> — never stdout, so a process whose
/// stdout is a wire protocol (Endpoints.Mcp JSON-RPC, Chess.Uci) can still carry a console
/// sink without corrupting its output. Shared by every console deployable via
/// <see cref="LaplaceLogging"/>; formerly hand-rolled per-project (GH #602).
/// </summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _min;
    public StderrLoggerProvider(LogLevel min = LogLevel.Information) => _min = min;
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, _min);
    public void Dispose() { }

    private sealed class StderrLogger : ILogger
    {
        private static readonly object Gate = new();
        private readonly string _category;
        private readonly LogLevel _min;

        public StderrLogger(string category, LogLevel min)
        {
            int dot = category.LastIndexOf('.');
            _category = dot >= 0 ? category[(dot + 1)..] : category;
            _min = min;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel level) => level >= _min && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string lvl = level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???",
            };
            string msg = formatter(state, ex);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {lvl} {_category}: {msg}"
                          + (ex is null ? "" : $"\n    {ex.GetType().Name}: {ex.Message}");
            lock (Gate) Console.Error.WriteLine(line);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
