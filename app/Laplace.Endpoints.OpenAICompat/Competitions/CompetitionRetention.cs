using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.Competitions;

/// <summary>How long an event is kept, and whether its workspace may still change it.</summary>
/// <param name="Frozen">The workspace had a plan that included hosting and it has ended: its events are read-only.</param>
/// <param name="DeleteOn">When the event is deleted, or null while a plan keeps it.</param>
internal readonly record struct Retention(bool Frozen, DateTimeOffset? DeleteOn);

/// <summary>
/// A plan that includes hosting keeps a workspace's events. Without one, an event is kept for
/// <see cref="Days"/> days after it was played; after a plan ends, everything is read-only and is
/// kept for the same number of days from the day the plan ended.
/// </summary>
internal static class CompetitionRetention
{
    public const int Days = 90;

    public static Task<Retention> OfWorkspaceAsync(NpgsqlDataSource application, string tenant, CancellationToken ct) =>
        ReadAsync(NpgsqlCatalog.Command(application, "competition.hold_tenant", tenant, CompetitionEndpoints.HostService, Days), ct);

    public static Task<Retention> OfEventAsync(NpgsqlDataSource application, Guid id, CancellationToken ct) =>
        ReadAsync(NpgsqlCatalog.Command(application, "competition.hold", id, CompetitionEndpoints.HostService, Days), ct);

    /// <summary>The refusal a frozen workspace gets for any change, or null when changes are allowed.</summary>
    public static IResult? Refusal(Retention retention) => !retention.Frozen ? null : Results.Json(
        new ErrorResponse(new ErrorBody("competition_error", "account_frozen",
            $"This workspace's plan has ended, so its events are read-only. They will be deleted on {retention.DeleteOn:d MMMM yyyy} unless the plan is renewed.")),
        statusCode: StatusCodes.Status403Forbidden);

    private static async Task<Retention> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(ct))
            return await reader.ReadAsync(ct)
                ? new Retention(reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1))
                : default;
    }
}

/// <summary>Deletes events whose keeping time has run out. Runs a few times a day; a failed pass is retried on the next.</summary>
internal sealed class CompetitionRetentionService(IServiceProvider services, ILogger<CompetitionRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                // Resolved here so a host without an application database logs a failed pass instead of failing to start.
                var application = services.GetRequiredKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
                await using var events = NpgsqlCatalog.Command(application, "competition.purge", CompetitionEndpoints.HostService, CompetitionRetention.Days);
                var removed = await events.ExecuteNonQueryAsync(stoppingToken);
                await using var sessions = NpgsqlCatalog.Command(application, "competition.purge_sessions", CompetitionEndpoints.HostService, CompetitionRetention.Days);
                await sessions.ExecuteNonQueryAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Deleted {Count} events past their keeping time", removed);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                logger.LogWarning(failure, "Event retention pass failed; it runs again on the next pass");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
