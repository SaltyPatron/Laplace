namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Fills the explore-catalog cache once at startup so the first UI landing hit never pays
/// the cold load (the bounded exact-aggregate attempts cost ~15s live). Failure is
/// non-fatal: the first request then loads it synchronously as before.
///
/// The chess roster used to be warmed here too. It no longer needs to be: each game folds
/// its result onto the player at ingest, so the ranking is an indexed read of consensus
/// cells rather than a ten-second corpus aggregate. Nothing to warm is better than a warm
/// cache.
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
