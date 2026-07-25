using Microsoft.Extensions.Logging;

namespace Laplace.Engine.Core.Ops;

/// <summary>
/// The one shared logging foundation for every Laplace console deployable (Cli, Migrations,
/// Endpoints.Mcp, Chess.Uci, and the OpenAICompat host) — GH #602. Both factories write the
/// CSV ops sink (<see cref="OpsLogFileLoggerProvider"/>, read back as SQL through ops.app_log);
/// they differ only in whether a human-facing console sink rides along:
///
///   ConsoleAndFile(role) — stderr text + CSV file. For apps whose stdout is free (Cli,
///                          Migrations, the API).
///   FileOnly(role)       — CSV file only. For apps whose stdout is a wire protocol
///                          (Endpoints.Mcp JSON-RPC, Chess.Uci) — nothing but the sink,
///                          so no diagnostic can leak onto the protocol stream.
///
/// role becomes the CSV application_name and the per-role filename laplace-{role}.csv.
///
/// The factory is hand-rolled on Microsoft.Extensions.Logging.Abstractions alone so
/// Laplace.Core keeps its single, near-BCL package dependency — it does not pull in the
/// Microsoft.Extensions.Logging implementation, DI, or Hosting into a library 10 projects
/// reference. A host-based consumer (the Spectre CLI, GH #603) can add these same providers
/// to its own ILoggingBuilder directly.
/// </summary>
public static class LaplaceLogging
{
    public static ILoggerFactory ConsoleAndFile(string role, LogLevel min = LogLevel.Information)
        => new LaplaceLoggerFactory(
            new StderrLoggerProvider(min),
            new OpsLogFileLoggerProvider(role, min));

    public static ILoggerFactory FileOnly(string role, LogLevel min = LogLevel.Information)
        => new LaplaceLoggerFactory(new OpsLogFileLoggerProvider(role, min));

    private sealed class LaplaceLoggerFactory : ILoggerFactory
    {
        private readonly List<ILoggerProvider> _providers;
        public LaplaceLoggerFactory(params ILoggerProvider[] providers) => _providers = new(providers);

        public void AddProvider(ILoggerProvider provider) => _providers.Add(provider);

        public ILogger CreateLogger(string categoryName)
            => new CompositeLogger(_providers.ConvertAll(p => p.CreateLogger(categoryName)));

        public void Dispose()
        {
            foreach (var p in _providers) p.Dispose();
        }
    }

    // Fans one record out to every provider's logger. Enabled if ANY sink is enabled;
    // each sink re-checks its own level in Log, so a disabled one is a cheap no-op.
    private sealed class CompositeLogger : ILogger
    {
        private readonly List<ILogger> _loggers;
        public CompositeLogger(List<ILogger> loggers) => _loggers = loggers;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel level)
        {
            foreach (var l in _loggers) if (l.IsEnabled(level)) return true;
            return false;
        }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            foreach (var l in _loggers) l.Log(level, eventId, state, ex, formatter);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
