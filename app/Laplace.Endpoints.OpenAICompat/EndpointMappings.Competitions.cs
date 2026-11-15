using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Endpoints.OpenAICompat.Competitions;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Hosted competitions. A workspace creates an event and shares its link; anyone with the link can
/// read it and register while registration is open; the host starts it and records results. Only
/// the hosting fee is ever charged here. Entry fees and prizes are the host's own affair.
/// </summary>
internal static class CompetitionEndpoints
{
    /// <summary>The service a workspace pays for to host an event larger than the free size.</summary>
    public const string HostService = "competition.host";
    public const int FreeEntrants = 8;
    public const int MaxEntrants = 256;

    /// <summary>The two routes a visitor with only the link may use: read the event, and register for it.</summary>
    public static bool IsPublic(HttpRequest request, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4) || !parts[0].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("competitions", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(parts[2], out _))
            return false;
        return parts.Length == 3
            ? HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            : HttpMethods.IsPost(request.Method) && parts[3].Equals("entrants", StringComparison.OrdinalIgnoreCase);
    }

    public static void MapCompetitionEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/competitions/formats", () => Results.Ok(new
        {
            free_entrants = FreeEntrants,
            max_entrants = MaxEntrants,
            host_service = HostService,
            formats = CompetitionFormats.All.Select(format => new { id = format.Id, title = format.Title, summary = format.Summary }),
        })).WithTags("competitions");

        app.MapGet("/v1/competitions", async (HttpContext http, ITenantResolver resolver,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            var competitions = new List<object>();
            await using var command = NpgsqlCatalog.Command(application, "competition.list", tenant);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                competitions.Add(new
                {
                    id = reader.GetGuid(0), title = reader.GetString(1), format = reader.GetString(2), status = reader.GetString(3),
                    created_at = reader.GetFieldValue<DateTimeOffset>(4), entrants = reader.GetInt64(5),
                });
            return Results.Ok(new { competitions });
        }).WithTags("competitions");

        app.MapPost("/v1/competitions", async (HttpContext http, NewCompetition request, ITenantResolver resolver,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            if (Clean(request.Title, 120) is not { } title) return Error(400, "invalid_title", "Give the event a title of up to 120 characters.");
            if (CompetitionFormats.Find(request.Format) is not { } format) return Error(400, "invalid_format", "Choose one of the listed formats.");
            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            var id = Guid.NewGuid();
            await using var command = NpgsqlCatalog.Command(application, "competition.create", id, tenant, title, format.Id);
            await command.ExecuteNonQueryAsync(ct);
            return Results.Created($"/v1/competitions/{id}", new { id });
        }).WithTags("competitions");

        app.MapGet("/v1/competitions/{id:guid}", async (Guid id, HttpContext http, ITenantResolver resolver, IOptions<LaplaceAuthOptions> auth,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            await using var connection = await application.OpenConnectionAsync(ct);
            string owner, title, formatId, status;
            await using (var command = NpgsqlCatalog.Command(connection, null, "competition.get", id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return NotFound();
                (owner, title, formatId, status) = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            }
            var entrants = new List<object>();
            await using (var command = NpgsqlCatalog.Command(connection, null, "competition.entrants", id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    entrants.Add(new { id = reader.GetGuid(0), name = reader.GetString(1), seed = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2) });

            var matches = new List<MatchView>();
            await using (var command = NpgsqlCatalog.Command(connection, null, "competition.matches", id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    var matchId = reader.GetString(0);
                    if (matches.Count == 0 || matches[^1].Id != matchId)
                        matches.Add(new MatchView(matchId, reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), []));
                    matches[^1].Slots.Add(new SlotView(
                        reader.IsDBNull(6) ? null : reader.GetGuid(6),
                        reader.IsDBNull(7) ? null : new SourceView(reader.GetString(7), reader.GetInt16(8)),
                        reader.IsDBNull(9) ? null : reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetInt16(10)));
                }

            var format = CompetitionFormats.Find(formatId);
            var rounds = matches.GroupBy(match => match.Stage).ToDictionary(stage => stage.Key, stage => stage.Max(match => match.Round));
            var standings = new List<object>();
            if (matches.Count > 0 && matches.All(match => match.Slots.Count == 2 && match.Slots.All(slot => slot.From is null)))
            {
                var rows = new List<object>();
                await using var command = NpgsqlCatalog.Command(connection, null, "competition.record", id);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add(new { entrant = reader.GetGuid(0), cells = new[] { reader.GetInt64(1).ToString(), reader.GetInt64(2).ToString() } });
                if (rows.Count > 0) standings.Add(new { stage = matches[0].Stage, columns = new[] { "W", "L" }, rows });
            }

            var caller = await resolver.ResolveAsync(http, ct);
            var signedIn = !auth.Value.RequiresIdentity || caller.AuthKind is "browser" or "api_key" or "dev";
            return Results.Ok(new
            {
                id, title, format = formatId, status,
                host = signedIn && string.Equals(caller.TenantId, owner, StringComparison.Ordinal),
                entrants,
                stages = format?.Stages(rounds).Select(stage => new { id = stage.Id, title = stage.Title, rounds = stage.Rounds }),
                matches = matches.Select(match => new
                {
                    id = match.Id, stage = match.Stage, round = match.Round, name = match.Name, state = match.State,
                    slots = match.Slots.Select(slot => new { entrant = slot.Entrant, from = slot.From, score = slot.Score, place = slot.Place }),
                }),
                standings,
            });
        }).WithTags("competitions");

        app.MapDelete("/v1/competitions/{id:guid}", async (Guid id, HttpContext http, ITenantResolver resolver,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            await using var command = NpgsqlCatalog.Command(application, "competition.delete", id, tenant);
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : NotFound();
        }).WithTags("competitions");

        app.MapPost("/v1/competitions/{id:guid}/entrants", async (Guid id, NewEntrant request,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            if (Clean(request.Name, 60) is not { } name) return Error(400, "invalid_name", "Enter a name of up to 60 characters.");
            var entrant = Guid.NewGuid();
            await using (var command = NpgsqlCatalog.Command(application, "competition.register", id, entrant, name, MaxEntrants))
                if (await command.ExecuteNonQueryAsync(ct) == 1) return Results.Created($"/v1/competitions/{id}", new { id = entrant, name });
            await using var lookup = NpgsqlCatalog.Command(application, "competition.get", id);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return NotFound();
            return reader.GetString(4) != "registration"
                ? Error(409, "registration_closed", "Registration for this event has closed.")
                : Error(409, "registration_refused", "That name is already registered, or the event is full.");
        }).RequireRateLimiting("public-query").WithTags("competitions");

        app.MapDelete("/v1/competitions/{id:guid}/entrants/{entrant:guid}", async (Guid id, Guid entrant, HttpContext http, ITenantResolver resolver,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            await using var command = NpgsqlCatalog.Command(application, "competition.withdraw", id, entrant, tenant);
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent()
                : Error(409, "withdraw_refused", "That entrant is not registered, or the event has already started.");
        }).WithTags("competitions");

        app.MapPost("/v1/competitions/{id:guid}/start", async (Guid id, HttpContext http, ITenantResolver resolver, IBillingOrchestrator billing,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            await using var connection = await application.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (await LockAsync(connection, transaction, id, tenant, ct) is not { } locked) return NotFound();
            if (locked.Status != "registration") return Error(409, "already_started", "This event has already started.");
            if (CompetitionFormats.Find(locked.Format) is not { } format) return Error(409, "unknown_format", "This event's format is no longer offered.");

            var entrants = new List<Guid>();
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.entrants", id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) entrants.Add(reader.GetGuid(0));
            if (entrants.Count < 2) return Error(409, "too_few_entrants", "An event needs at least two entrants before it can start.");

            QuoteExecutionGate? gate = null;
            if (entrants.Count > FreeEntrants)
            {
                gate = await QuoteGate.RequireQuoteAsync(http.Request, billing, HostService, ct);
                if (!gate.Allowed)
                    return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null
                        ? new QuoteServiceDetail(HostService)
                        : (object)new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));
            }

            var plan = format.Plan(entrants);
            var slots = plan.SelectMany(match => match.Slots.Select((slot, position) => (match.Id, Position: (short)position, slot))).ToArray();
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.seed", id, entrants.ToArray()))
                await command.ExecuteNonQueryAsync(ct);
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.put_matches", id,
                plan.Select(match => match.Id).ToArray(), Enumerable.Range(0, plan.Count).ToArray(),
                plan.Select(match => match.Stage).ToArray(), plan.Select(match => match.Round).ToArray(),
                plan.Select(match => match.Name).ToArray()))
                await command.ExecuteNonQueryAsync(ct);
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.put_slots", id,
                slots.Select(s => s.Id).ToArray(), slots.Select(s => s.Position).ToArray(),
                slots.Select(s => s.slot.Entrant).ToArray(), slots.Select(s => s.slot.FromMatch).ToArray(),
                slots.Select(s => s.slot.FromPlace).ToArray()))
                await command.ExecuteNonQueryAsync(ct);
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.start", id))
                await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            if (gate?.Quote is not null) await billing.MarkConsumedAndRecordAsync(gate.Quote, ct);
            return Results.Ok(new { id, matches = plan.Count });
        }).WithTags("competitions");

        app.MapPut("/v1/competitions/{id:guid}/matches/{match}/result", async (Guid id, string match, MatchResult request,
            HttpContext http, ITenantResolver resolver,
            [FromKeyedServices(AppComposition.ApplicationDatabaseKey)] Npgsql.NpgsqlDataSource application, CancellationToken ct) =>
        {
            var result = request.Slots ?? [];
            var places = result.Select(slot => slot.Place).Order().ToArray();
            if (result.Length < 2 || !places.SequenceEqual(Enumerable.Range(1, result.Length).Select(n => (short)n)))
                return Error(400, "invalid_places", "Give every entrant in the match a different finishing place, starting from 1.");
            var scores = new string?[result.Length];
            for (var i = 0; i < result.Length; i++)
            {
                if (result[i].Score is null) continue;
                if (Clean(result[i].Score, 16) is not { } score) return Error(400, "invalid_score", "A score is at most 16 characters.");
                scores[i] = score;
            }

            var tenant = (await resolver.ResolveAsync(http, ct)).TenantId;
            await using var connection = await application.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (await LockAsync(connection, transaction, id, tenant, ct) is not { } locked) return NotFound();
            if (locked.Status != "running") return Error(409, "not_started", "Start the event before recording results.");
            long total, filled;
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.match_slots", id, match))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                (total, filled) = (reader.GetInt64(0), reader.GetInt64(1));
            }
            if (total == 0) return NotFound();
            if (total != result.Length) return Error(400, "wrong_slot_count", $"This match has {total} entrants; send a result for each.");
            if (filled != total) return Error(409, "match_not_ready", "This match is still waiting on an earlier result.");
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.finish_match", id, match))
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    return Error(409, "later_result_recorded", "A later match already has a result that depends on this one. Change that result first.");
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.put_result", id, match,
                Enumerable.Range(0, result.Length).Select(position => (short)position).ToArray(), scores, result.Select(slot => slot.Place).ToArray()))
                await command.ExecuteNonQueryAsync(ct);
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "competition.advance", id, match))
                await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).WithTags("competitions");
    }

    private static async Task<(string Format, string Status)?> LockAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, Guid id, string tenant, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, transaction, "competition.lock", id, tenant);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static string? Clean(string? text, int max)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > max || value.Any(char.IsControl) ? null : value;
    }

    private static IResult NotFound() => Error(404, "competition_not_found", "No such event exists.");
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(new ErrorBody("competition_error", code, message)), statusCode: status);

    private sealed record MatchView(string Id, string Stage, int Round, string Name, string State, List<SlotView> Slots);
    private sealed record SlotView(Guid? Entrant, SourceView? From, string? Score, short? Place);
    private sealed record SourceView(string Match, short Place);

    internal sealed record NewCompetition(string? Title, string? Format);
    internal sealed record NewEntrant(string? Name);
    internal sealed record MatchResult(SlotResult[]? Slots);
    internal sealed record SlotResult(string? Score, short Place);
}
