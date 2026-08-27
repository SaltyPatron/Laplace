using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

/// <summary>
/// Lichess bot session: per-ply substrate fold before search; chat ring buffer per game.
/// </summary>
public interface ILichessConnection : IAsyncDisposable
{
    LichessConnectivityStatus Status();
    IReadOnlyList<LichessChatLine> ChatForGame(string gameId);
    bool Start(int depth = 8, int maxConcurrent = 2, bool substrate = true, IReadOnlySet<string>? acceptSpeeds = null);
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
    private int _depth = 8;
    private int _maxConcurrent = 2;
    private bool _substrate = true;
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
            bool connected = _connected && _runTask is not null && !_runTask.IsCompleted;
            return new LichessConnectivityStatus(
                Configured: configured,
                TokenPreview: null,
                Connected: connected,
                Username: _username,
                Depth: _depth,
                MaxConcurrent: _maxConcurrent,
                Substrate: _substrate,
                GamesRecorded: _gamesRecorded,
                RecentLog: _recentLog.ToArray(),
                Error: _lastError,
                Running: _runTask is not null && !_runTask.IsCompleted);
        }
    }

    public IReadOnlyList<LichessChatLine> ChatForGame(string lichessGameId)
    {
        if (!_chatByGame.TryGetValue(lichessGameId, out var q)) return Array.Empty<LichessChatLine>();
        return q.ToArray();
    }

    public bool Start(int depth = 8, int maxConcurrent = 2, bool substrate = true, IReadOnlySet<string>? acceptSpeeds = null)
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

            _depth = Math.Max(1, depth);
            _maxConcurrent = Math.Max(1, maxConcurrent);
            _substrate = substrate;
            _lastError = null;
            _connected = false;
            _username = null;
            _cts = new CancellationTokenSource();
            var lifetime = _cts.Token;
            _runTask = Task.Run(() => RunAsync(token, acceptSpeeds, lifetime));
        }

        _log.LogInformation("lichess connectivity starting (depth {Depth}, max {Max}, substrate {Substrate})",
            depth, maxConcurrent, substrate);
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

    private async Task RunAsync(string token, IReadOnlySet<string>? acceptSpeeds, CancellationToken ct)
    {
        try
        {
            var user = await LichessBot.FetchUsernameAsync(token, ct);
            if (user is null) throw new InvalidOperationException("Lichess account authentication failed; verify token and bot scope.");
            var host = _host ??= await _getHost(ct);
            lock (_gate) { _username = user; }
            PushLog(user is not null
                ? $"online as @{user} — per-ply fold before each search; games record live"
                : "connected (could not resolve username — check token scopes)");

            if (_substrate)
                PushLog("substrate fold bias + learned PST refresh after each ply fold");
            else
                PushLog("substrate bias off — classical search, still recording if enabled");

            await using var bot = new LichessBot(
                token,
                host,
                substrate: _substrate,
                record: true,
                maxDepth: _depth,
                botUsername: user,
                onChatLine: line =>
                {
                    var q = _chatByGame.GetOrAdd(line.GameId, _ => new ConcurrentQueue<LichessChatLine>());
                    q.Enqueue(line);
                    while (q.Count > MaxChatLines && q.TryDequeue(out _)) { }
                    PushLog($"chat [{line.Room}] @{line.Username}: {line.Text}");
                },
                acceptSpeeds: acceptSpeeds,
                onConnectionChanged: connected => { lock (_gate) { _connected = connected; } },
                log: new QueueLogger(this));

            await bot.RunAsync(_maxConcurrent, ct);
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
    int Depth,
    int MaxConcurrent,
    bool Substrate,
    long GamesRecorded,
    IReadOnlyList<string> RecentLog,
    string? Error,
    bool Running = false);
