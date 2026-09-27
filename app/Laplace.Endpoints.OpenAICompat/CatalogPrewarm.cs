namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Loads the explore catalog once at startup so the first request finds it cached. On
/// failure the first request loads it instead.
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
