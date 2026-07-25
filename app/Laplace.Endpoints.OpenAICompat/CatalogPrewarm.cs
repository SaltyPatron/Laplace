namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Fills the corpus-wide aggregate caches once at startup so no first UI hit pays a
/// cold load: the explore catalog (bounded exact-aggregate attempts, ~15s live) and
/// the chess roster (every game header in the corpus, ~10s live). Both are read-only
/// substrate accounting that only moves when an ingest lands, and both are warmed
/// independently — one failing must not cost the other its warm cache. Failure is
/// non-fatal either way: the first request then loads it synchronously as before.
/// </summary>
internal sealed class CatalogPrewarmService : BackgroundService
{
    private readonly ISubstrateClient _substrate;
    private readonly ILogger<CatalogPrewarmService> _logger;

    public CatalogPrewarmService(ISubstrateClient substrate, ILogger<CatalogPrewarmService> logger)
    {
        _substrate = substrate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WarmAsync("explore catalog",
            () => _substrate.ExploreCatalogAsync(stoppingToken), stoppingToken);
        await WarmAsync("chess roster",
            () => _substrate.ChessRosterAsync(stoppingToken), stoppingToken);
    }

    private async Task WarmAsync(string what, Func<Task> load, CancellationToken ct)
    {
        try
        {
            await load();
            _logger.LogInformation("{What} prewarmed", what);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{What} prewarm failed; first request will load it", what);
        }
    }
}
