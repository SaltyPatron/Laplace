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
        await using var command = database.CreateCommand("SELECT role FROM app.platform_roles WHERE user_id = $1");
        command.Parameters.AddWithValue(user);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct)
    {
        var managed = owners is null ? new HashSet<Guid>() : await owners.OwnerUsersAsync(ct);
        await using var command = database.CreateCommand("""
            SELECT u.user_id, u.display_name, u.email, r.role,
                ARRAY(SELECT DISTINCT e.provider FROM app.external_identities e WHERE e.user_id = u.user_id ORDER BY e.provider)
            FROM app.users u LEFT JOIN app.platform_roles r USING(user_id)
            WHERE $1 = '' OR strpos(lower(coalesce(u.display_name, '') || ' ' || coalesce(u.email, '') || ' ' || u.user_id::text), lower($1)) > 0
            ORDER BY r.role IS NULL, u.display_name NULLS LAST, u.user_id LIMIT 100
            """);
        command.Parameters.AddWithValue(search);
        var result = new List<PlatformPerson>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), reader.GetFieldValue<string[]>(4), managed.Contains(reader.GetGuid(0))));
        return result;
    }

    public async Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            SELECT e.event_id, e.user_id, e.actor, e.previous_role, e.role, e.occurred_at,
                coalesce(person.email, person.display_name), coalesce(actor.email, actor.display_name)
            FROM app.platform_role_events e
            LEFT JOIN app.users person ON person.user_id = e.user_id
            LEFT JOIN app.users actor ON actor.user_id::text = e.actor
            ORDER BY e.event_id DESC LIMIT 50
            """);
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
        await using (var locked = new NpgsqlCommand("LOCK TABLE app.platform_roles IN SHARE ROW EXCLUSIVE MODE", connection, transaction))
            await locked.ExecuteNonQueryAsync(ct);
        if (actor is { } who)
        {
            await using var authorized = new NpgsqlCommand("SELECT role FROM app.platform_roles WHERE user_id = $1", connection, transaction);
            authorized.Parameters.AddWithValue(who);
            if (await authorized.ExecuteScalarAsync(ct) as string != "administrator") throw new AccessChangeException("Your platform access changed. Refresh before continuing.");
        }
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM app.users WHERE user_id = $1", connection, transaction))
        {
            exists.Parameters.AddWithValue(user);
            if (await exists.ExecuteScalarAsync(ct) is null) throw new AccessChangeException("That person must sign in to Laplace before access can be assigned.");
        }
        if (role != "administrator" && owners is not null && await owners.IsOwnerAsync(connection, transaction, user, ct))
            throw new AccessChangeException("This administrator is managed by the installation owner policy. Change that policy before removing owner access.");
        string? previous;
        await using (var current = new NpgsqlCommand("SELECT role FROM app.platform_roles WHERE user_id = $1", connection, transaction))
        {
            current.Parameters.AddWithValue(user);
            previous = await current.ExecuteScalarAsync(ct) as string;
        }
        if (onlyIfNeverAssigned)
        {
            await using var history = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM app.platform_role_events WHERE user_id = $1)", connection, transaction);
            history.Parameters.AddWithValue(user);
            if (previous is not null || (bool)(await history.ExecuteScalarAsync(ct))!)
                throw new AccessChangeException("Initial access assignment requires a person with no current role or previous role changes.");
        }
        if (previous == role) { await transaction.CommitAsync(ct); return; }
        if (previous == "administrator" && role != "administrator")
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM app.platform_roles WHERE role = 'administrator'", connection, transaction);
            if ((long)(await count.ExecuteScalarAsync(ct))! <= 1) throw new AccessChangeException("Keep at least one platform administrator. Assign another administrator first.");
        }
        await using (var update = new NpgsqlCommand(role is null
            ? "DELETE FROM app.platform_roles WHERE user_id = $1"
            : "INSERT INTO app.platform_roles(user_id, role) VALUES($1, $2) ON CONFLICT(user_id) DO UPDATE SET role = excluded.role, updated_at = now()", connection, transaction))
        {
            update.Parameters.AddWithValue(user);
            if (role is not null) update.Parameters.AddWithValue(role);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = new NpgsqlCommand("INSERT INTO app.platform_role_events(user_id, actor, previous_role, role) VALUES($1, $2, $3, $4)", connection, transaction))
        {
            audit.Parameters.AddWithValue(user);
            audit.Parameters.AddWithValue(actor?.ToString("D") ?? "installation-credential");
            audit.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)previous ?? DBNull.Value);
            audit.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)role ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
