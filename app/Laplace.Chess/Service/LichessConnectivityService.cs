using System.Collections.Concurrent;
using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public static class LichessDefaults
{
    public const int MaxConcurrent = 2;
}

/// <summary>
/// Lichess bot session: per-ply substrate fold before search; chat ring buffer per game.
/// </summary>
public interface ILichessConnection : IAsyncDisposable
{
    LichessConnectivityStatus Status();
    IReadOnlyList<LichessChatLine> ChatForGame(string gameId);
    bool Start(int maxConcurrent = LichessDefaults.MaxConcurrent, LichessChallengePolicy? policy = null,
        ILichessEngine? engine = null);
    Task WaitForExitAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed class LichessConnectivityService : ILichessConnection
{
    private const int MaxLogLines = 64;
    private const int MaxChatLines = 32;
    private readonly Func<CancellationToken, Task<ChessLiveGameHost>> _getHost;
    private ChessLiveGameHost? _host;
    private readonly ILogger _log;
    private readonly bool _ownsHost;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _recentLog = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LichessChatLine>> _chatByGame = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private string? _username;
    private string? _lastError;
    private bool _connected;
    private LichessAccountReadiness? _account;
    private int _maxConcurrent = LichessDefaults.MaxConcurrent;
    private ILichessEngine? _engine;
    private EngineIdentity? _engineIdentity;
    private long _gamesRecorded;

    public LichessConnectivityService(ChessLiveGameHost host, ILogger? log = null)
        : this(_ => Task.FromResult(host), log)
    {
        _host = host;
    }

    public LichessConnectivityService(
        Func<CancellationToken, Task<ChessLiveGameHost>> getHost, ILogger? log = null, bool ownsHost = false)
    {
        _getHost = getHost;
        _log = log ?? NullLogger.Instance;
        _ownsHost = ownsHost;
    }

    public LichessConnectivityStatus Status()
    {
        lock (_gate)
        {
            var token = LichessBot.ResolveToken();
            bool configured = !string.IsNullOrEmpty(token);
            bool connected = _account?.Ready == true && _connected && _runTask is not null && !_runTask.IsCompleted;
            return new LichessConnectivityStatus(
                Configured: configured,
                TokenPreview: null,
                Connected: connected,
                Username: _username,
                Engine: _engine?.Name ?? "laplace",
                MaxConcurrent: _maxConcurrent,
                GamesRecorded: _gamesRecorded,
                RecentLog: _recentLog.ToArray(),
                Error: _lastError,
                Running: _runTask is not null && !_runTask.IsCompleted,
                Account: _account,
                EngineIdentity: _engineIdentity);
        }
    }

    public IReadOnlyList<LichessChatLine> ChatForGame(string lichessGameId)
    {
        if (!_chatByGame.TryGetValue(lichessGameId, out var q)) return Array.Empty<LichessChatLine>();
        return q.ToArray();
    }

    public bool Start(int maxConcurrent = LichessDefaults.MaxConcurrent,
        LichessChallengePolicy? policy = null, ILichessEngine? engine = null)
    {
        var token = LichessBot.ResolveToken();
        if (string.IsNullOrEmpty(token))
        {
            _lastError = "No Lichess token — set LICHESS_TOKEN or LICHESS_API in deploy/secrets/lichess.env (or env) and republish";
            PushLog(_lastError);
            return false;
        }

        lock (_gate)
        {
            if (_runTask is not null && !_runTask.IsCompleted)
                return false;

            _maxConcurrent = Math.Max(1, maxConcurrent);
            try { _engine = engine ?? UciLichessEngine.FromEnvironment(); }
            catch (Exception ex) when (ex is UciEngineException or FormatException)
            {
                _lastError = "Lichess engine not configured: " + ex.Message;
                PushLog(_lastError);
                return false;
            }
            _engineIdentity = null;
            _lastError = null;
            _connected = false;
            _username = null;
            _account = null;
            _cts = new CancellationTokenSource();
            var lifetime = _cts.Token;
            _runTask = Task.Run(() => RunAsync(token, policy, lifetime));
        }

        _log.LogInformation("lichess connectivity starting (engine {Engine}, max {Max})", _engine?.Name, maxConcurrent);
        PushLog("connecting…");
        return true;
    }

    public bool Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_runTask is null || _runTask.IsCompleted) return false;
            cts = _cts;
        }

        cts?.Cancel();
        PushLog("stop requested — bounded in-flight game drain…");
        _log.LogInformation("lichess connectivity stop requested");
        return true;
    }

    public Task WaitForExitAsync(CancellationToken ct)
    {
        lock (_gate) return (_runTask ?? Task.CompletedTask).WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Stop();
        await WaitForExitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_ownsHost && _host is not null) await _host.DisposeAsync();
    }

    private async Task RunAsync(string token, LichessChallengePolicy? policy, CancellationToken ct)
    {
        try
        {
            PushLog("verifying Lichess token, bot:play permission, and BOT account…");
            var account = await LichessAccountReadiness.CheckAsync(token, ct);
            lock (_gate) { _account = account; _username = account.Username; }
            if (!account.Ready) throw new InvalidOperationException(account.Error);
            var user = account.Username!;
            var host = _host ??= await _getHost(ct);
            PushLog($"BOT @{user} and bot:play permission verified; opening event stream…");

            PushLog($"engine {_engine!.Name} through the generic UCI client; recording is the same whichever engine plays");

            await using var bot = new LichessBot(
                token,
                host,
                engine: _engine,
                record: true,
                botUsername: user,
                onChatLine: line =>
                {
                    var q = _chatByGame.GetOrAdd(line.GameId, _ => new ConcurrentQueue<LichessChatLine>());
                    q.Enqueue(line);
                    while (q.Count > MaxChatLines && q.TryDequeue(out _)) { }
                    PushLog($"chat [{line.Room}] @{line.Username}: {line.Text}");
                },
                policy: policy,
                onConnectionChanged: connected =>
                {
                    lock (_gate)
                    {
                        if (_connected == connected) return;
                        _connected = connected;
                    }
                    PushLog(connected ? $"online as @{user}; event stream connected" : "event stream disconnected");
                },
                log: new QueueLogger(this),
                onEngineIdentity: identity => { lock (_gate) _engineIdentity = identity; });

            await bot.RunVerifiedAsync(account, _maxConcurrent, ct);
            PushLog("disconnected");
        }
        catch (OperationCanceledException)
        {
            PushLog("stopped");
        }
        catch (Exception ex)
        {
            lock (_gate) { _lastError = ex.Message; }
            PushLog($"error: {ex.Message}");
            _log.LogWarning(ex, "lichess connectivity failed");
        }
        finally
        {
            lock (_gate)
            {
                _gamesRecorded = _host?.GamesCompleted ?? _gamesRecorded;
                _connected = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    internal void PushLog(string line)
    {
        _recentLog.Enqueue(line);
        while (_recentLog.Count > MaxLogLines && _recentLog.TryDequeue(out _)) { }
    }

    private sealed class QueueLogger(LichessConnectivityService svc) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            svc.PushLog(formatter(state, exception));
            if (formatter(state, exception).Contains("recorded", StringComparison.OrdinalIgnoreCase))
                lock (svc._gate) { svc._gamesRecorded = svc._host?.GamesCompleted ?? svc._gamesRecorded; }
        }
    }
}

public sealed record LichessConnectivityStatus(
    bool Configured,
    string? TokenPreview,
    bool Connected,
    string? Username,
    string Engine,
    int MaxConcurrent,
    long GamesRecorded,
    IReadOnlyList<string> RecentLog,
    string? Error,
    bool Running = false,
    LichessAccountReadiness? Account = null,
    EngineIdentity? EngineIdentity = null);
