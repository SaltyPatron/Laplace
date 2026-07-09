namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Fills the explore-catalog cache once at startup so the first UI landing hit never
/// pays the cold load (the bounded exact-aggregate attempts cost ~15s live). Failure is
/// non-fatal: the first request then loads it synchronously as before.
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
        try
        {
            await _substrate.ExploreCatalogAsync(stoppingToken);
            _logger.LogInformation("explore catalog prewarmed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "explore catalog prewarm failed; first request will load it");
        }
    }
}
