using System.Security.Claims;
using Microsoft.Extensions.Options;
using Npgsql;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat.Auth;

internal sealed record PlatformRoleDefinition(string Id, string Name, string Description, string[] Permissions);
internal sealed record PlatformPerson(Guid UserId, string? DisplayName, string? Email, string? Role, string[] Providers, bool InstallationOwner = false);
internal sealed record PlatformRoleEvent(long EventId, Guid UserId, string Actor, string? PreviousRole, string? Role, DateTimeOffset OccurredAt, string? Person, string? ActorName);
internal sealed class AccessChangeException(string message) : Exception(message);

internal static class PlatformPolicy
{
    public const string Read = "platform.read";
    public const string Ingest = "ingestion.manage";
    public const string Services = "services.manage";
    public const string Maintain = "platform.maintain";
    public const string Access = "platform.access.manage";
    public static readonly PlatformRoleDefinition[] Roles =
    [
        new("observer", "Observer", "View ingestion progress and system activity.", [Read]),
        new("operator", "Operations manager", "Run ingestion and manage services; cannot assign platform access or perform database maintenance.", [Read, Ingest, Services]),
        new("administrator", "Platform administrator", "Manage platform access, ingestion, services, and database maintenance.", [Read, Ingest, Services, Maintain, Access])
    ];
    public static string[] Permissions(string? role) => Roles.FirstOrDefault(r => r.Id == role)?.Permissions ?? [];
    public static Guid? UserId(HttpContext http) => http.User.Identity?.IsAuthenticated == true
        && Guid.TryParse(http.User.FindFirstValue(LaplaceClaimTypes.User), out var user) ? user : null;
    public static string ForRoute(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/v1/admin/access")) return Access;
        if (path.StartsWithSegments("/v1/billing/operator") || path == "/v1/billing/catalog/sync") return Maintain;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return path.StartsWithSegments("/v1/admin/ingest") || path.StartsWithSegments("/v1/admin/services") ? Read : Maintain;
        if (path.StartsWithSegments("/v1/admin/ingest")) return Ingest;
        if (path.StartsWithSegments("/v1/admin/services")) return Services;
        return Maintain;
    }
    public static string? ForOperation(string name)
    {
        if (name is "ops.app_log" or "ops.chess_drops") return Read;
        if (name is "ops.repoint_app_log" or "ops.repoint_chess_drops" or "ops.repoint_pg_log") return Maintain;
        if (name == "ops.ingest_run_close") return Ingest;
        if (Laplace.SubstrateCRUD.Npgsql.InstalledOpInvoker.IsWritable(name)) return Maintain;
        if (name.StartsWith("ops.ingest_", StringComparison.Ordinal) || name.StartsWith("ops.activity", StringComparison.Ordinal)) return Read;
        return null;
    }
}

internal interface IPlatformAccessStore
{
    Task<string?> RoleAsync(Guid user, CancellationToken ct);
    Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct);
    Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct);
    Task SetRoleAsync(Guid user, string? role, Guid? actor, CancellationToken ct, bool onlyIfNeverAssigned = false);
}

internal sealed class PlatformAuthorization(IPlatformAccessStore store, IOptions<LaplaceAuthOptions> options)
{
    public async Task<bool> AllowsAsync(HttpContext http, string permission, CancellationToken ct)
    {
        if (OperatorAuth.HasCredential(http.Request, options.Value)) return true;
        if (ApiKeyTenantResolver.PresentedKey(http.Request) is not null
            || http.Request.Headers.ContainsKey("Authorization") || http.Request.Headers.ContainsKey("X-Api-Key")) return false;
        return PlatformPolicy.UserId(http) is { } user
            && PlatformPolicy.Permissions(await store.RoleAsync(user, ct)).Contains(permission);
    }
}

internal sealed class PostgresPlatformAccessStore(NpgsqlDataSource database, PlatformOwnerReconciler? owners = null) : IPlatformAccessStore
{
    public async Task<string?> RoleAsync(Guid user, CancellationToken ct)
    {
        var role = await StoredRoleAsync(user, ct);
        if (role != "administrator" && owners is not null && await owners.IsOwnerAsync(user, ct))
        {
            await owners.ReconcileUserAsync(user, ct);
            role = await StoredRoleAsync(user, ct);
        }
        return role;
    }

    private async Task<string?> StoredRoleAsync(Guid user, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(database, "access.stored_role", user);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct)
    {
        var managed = owners is null ? new HashSet<Guid>() : await owners.OwnerUsersAsync(ct);
        await using var command = NpgsqlCatalog.Command(database, "access.people", search);
        var result = new List<PlatformPerson>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), reader.GetFieldValue<string[]>(4), managed.Contains(reader.GetGuid(0))));
        return result;
    }

    public async Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(database, "access.events");
        var result = new List<PlatformRoleEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), reader.GetFieldValue<DateTimeOffset>(5), Text(reader, 6), Text(reader, 7)));
        return result;
    }

    public async Task SetRoleAsync(Guid user, string? role, Guid? actor, CancellationToken ct, bool onlyIfNeverAssigned = false)
    {
        if (role is not null && !PlatformPolicy.Roles.Any(r => r.Id == role)) throw new AccessChangeException("Choose a listed platform role.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize role changes so two administrators cannot both remove the last administrator.
        await using (var locked = NpgsqlCatalog.Command(connection, transaction, "access.lock_roles"))
            await locked.ExecuteNonQueryAsync(ct);
        if (actor is { } who)
        {
            await using var authorized = NpgsqlCatalog.Command(connection, transaction, "access.stored_role", who);
            if (await authorized.ExecuteScalarAsync(ct) as string != "administrator") throw new AccessChangeException("Your platform access changed. Refresh before continuing.");
        }
        await using (var exists = NpgsqlCatalog.Command(connection, transaction, "access.user_exists", user))
        {
            if (await exists.ExecuteScalarAsync(ct) is null) throw new AccessChangeException("That person must sign in to Laplace before access can be assigned.");
        }
        if (role != "administrator" && owners is not null && await owners.IsOwnerAsync(connection, transaction, user, ct))
            throw new AccessChangeException("This administrator is managed by the installation owner policy. Change that policy before removing owner access.");
        string? previous;
        await using (var current = NpgsqlCatalog.Command(connection, transaction, "access.stored_role", user))
        {
            previous = await current.ExecuteScalarAsync(ct) as string;
        }
        if (onlyIfNeverAssigned)
        {
            await using var history = NpgsqlCatalog.Command(connection, transaction, "access.has_role_events", user);
            if (previous is not null || (bool)(await history.ExecuteScalarAsync(ct))!)
                throw new AccessChangeException("Initial access assignment requires a person with no current role or previous role changes.");
        }
        if (previous == role) { await transaction.CommitAsync(ct); return; }
        if (previous == "administrator" && role != "administrator")
        {
            await using var count = NpgsqlCatalog.Command(connection, transaction, "access.admin_count");
            if ((long)(await count.ExecuteScalarAsync(ct))! <= 1) throw new AccessChangeException("Keep at least one platform administrator. Assign another administrator first.");
        }
        if (role is null)
        {
            await using var update = NpgsqlCatalog.Command(connection, transaction, "access.delete_role", user);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var update = NpgsqlCatalog.Command(connection, transaction, "access.upsert_role", user, role);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = NpgsqlCatalog.Command(
            connection, transaction, "access.insert_role_event",
            user, actor?.ToString("D") ?? "installation-credential", previous, role))
            await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
