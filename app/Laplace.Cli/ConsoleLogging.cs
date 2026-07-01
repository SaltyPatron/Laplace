using Microsoft.Extensions.Logging;

namespace Laplace.Cli;

public sealed class ConsoleLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _min;
    public ConsoleLoggerProvider(LogLevel min) => _min = min;
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, _min);
    public void Dispose() { }

    public static ILoggerFactory Factory(LogLevel min = LogLevel.Information) =>
        new SimpleFactory(min);

    private sealed class SimpleFactory : ILoggerFactory
    {
        private readonly LogLevel _min;
        public SimpleFactory(LogLevel min) => _min = min;
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, _min);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class ConsoleLogger : ILogger
    {
        private static readonly object Gate = new();
        private readonly string _category;
        private readonly LogLevel _min;
        public ConsoleLogger(string category, LogLevel min)
        {
            int colon = category.IndexOf(':');
            _category = colon >= 0 ? category[(colon + 1)..] : category;
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
                _ => "???"
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
