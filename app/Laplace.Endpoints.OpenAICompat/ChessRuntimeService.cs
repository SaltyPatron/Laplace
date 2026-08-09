using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Generic-Host-owned lifetime for the shared chess datasource/write spine.
/// Construction is cheap; the first operation that actually needs the substrate starts
/// initialization asynchronously. Status and pure endpoints never perform database work
/// merely because DI resolved one of their services.
/// </summary>
internal sealed class ChessRuntimeService : IHostedService, IAsyncDisposable
{
    private const double WitnessWeight = 0.5d;

    private readonly ILogger<ChessRuntimeService> _log;
    private readonly Func<CancellationToken, Task<ChessLiveGameHost>> _createHost;
    private readonly object _gate = new();
    private CancellationTokenSource? _stopping;
    private Task<ChessLiveGameHost>? _initialization;
    private ChessLiveGameHost? _host;

    public ChessRuntimeService(ILogger<ChessRuntimeService> log)
        : this(log, ct => ChessLiveGameHost.CreateAsync(WitnessWeight, ct: ct))
    {
    }

    internal ChessRuntimeService(
        ILogger<ChessRuntimeService> log,
        Func<CancellationToken, Task<ChessLiveGameHost>> createHost)
    {
        _log = log;
        _createHost = createHost;
    }

    internal bool InitializationStarted
    {
        get { lock (_gate) return _initialization is not null; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? prior;
        lock (_gate)
        {
            if (_stopping is { IsCancellationRequested: false })
                return Task.CompletedTask;
            prior = _stopping;
            _stopping = new CancellationTokenSource();
        }
        prior?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<ChessLiveGameHost> GetAsync(CancellationToken ct)
    {
        Task<ChessLiveGameHost> initialization;
        lock (_gate)
        {
            var lifetime = _stopping
                ?? throw new InvalidOperationException("chess runtime has not started");
            lifetime.Token.ThrowIfCancellationRequested();
            // A connection/schema failure is not a process-lifetime state. Every caller in
            // one attempt shares the same task, while the first caller after a fault starts
            // one fresh attempt instead of requiring an application restart.
            if (_initialization is { IsFaulted: true } or { IsCanceled: true })
                _initialization = null;
            initialization = _initialization ??= InitializeAsync(lifetime.Token);
        }

        return await initialization.WaitAsync(ct);
    }

    private async Task<ChessLiveGameHost> InitializeAsync(CancellationToken ct)
    {
        try
        {
            var host = await _createHost(ct);
            lock (_gate)
            {
                if (!ct.IsCancellationRequested)
                    _host = host;
            }
            if (ct.IsCancellationRequested)
            {
                await host.DisposeAsync();
                ct.ThrowIfCancellationRequested();
            }
            _log.LogInformation("chess runtime initialized");
            return host;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "chess runtime initialization failed");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task<ChessLiveGameHost>? initialization;
        lock (_gate)
        {
            _stopping?.Cancel();
            initialization = _initialization;
            _initialization = null;
        }

        if (initialization is not null)
        {
            try { await initialization.WaitAsync(cancellationToken); }
            catch (Exception) { }
        }

        await DisposeHostAsync();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stopping;
        Task<ChessLiveGameHost>? initialization;
        lock (_gate)
        {
            stopping = _stopping;
            _stopping = null;
            initialization = _initialization;
            _initialization = null;
        }
        stopping?.Cancel();
        if (initialization is not null)
        {
            try { await initialization; }
            catch (Exception) { }
        }
        await DisposeHostAsync();
        stopping?.Dispose();
    }

    private async ValueTask DisposeHostAsync()
    {
        ChessLiveGameHost? host;
        lock (_gate) { host = _host; _host = null; }
        if (host is not null) await host.DisposeAsync();
    }
}
