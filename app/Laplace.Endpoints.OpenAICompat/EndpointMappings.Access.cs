using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AccessEndpoints
{
    public static void MapAccessEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/access", async (HttpContext http, ITenantResolver resolver, IPlatformAccessStore access,
            IOptions<LaplaceAuthOptions> options, CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(http, ct);
            var user = tenant.AuthKind == "browser" ? PlatformPolicy.UserId(http) : null;
            var installationCredential = OperatorAuth.HasCredential(http.Request, options.Value);
            var role = installationCredential ? "administrator"
                : user is { } id ? await access.RoleAsync(id, ct) : null;
            var workspaceRole = http.Items.TryGetValue("laplace.workspace_role", out var value) ? value as string : null;
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new
            {
                authenticated = installationCredential || tenant.AuthKind is "browser" or "api_key" or "dev", userId = user,
                workspace = new { tenantId = tenant.TenantId, role = workspaceRole },
                platform = new { role, permissions = PlatformPolicy.Permissions(role) },
                roles = PlatformPolicy.Roles
            });
        }).WithTags("access");

        app.MapGet("/v1/admin/access/capabilities", () => Results.Ok(new { initialRoleAssignment = true, persistentInstallationOwners = true }))
            .WithTags("access");
        app.MapGet("/v1/admin/access/people", async (string? search, IPlatformAccessStore access, CancellationToken ct) =>
        {
            var term = (search ?? "").Trim();
            return Results.Ok(new { people = await access.PeopleAsync(term[..Math.Min(term.Length, 160)], ct), limit = 100 });
        })
            .WithTags("access");
        app.MapGet("/v1/admin/access/events", async (IPlatformAccessStore access, CancellationToken ct) =>
            Results.Ok(new { events = await access.EventsAsync(ct), limit = 50 })).WithTags("access");
        app.MapPut("/v1/admin/access/people/{userId:guid}/role", async (Guid userId, PlatformRoleRequest request,
            HttpContext http, IPlatformAccessStore access, IOptions<LaplaceAuthOptions> options, CancellationToken ct) =>
        {
            try
            {
                await access.SetRoleAsync(userId, request.Role,
                    OperatorAuth.HasCredential(http.Request, options.Value) ? null : PlatformPolicy.UserId(http), ct,
                    request.OnlyIfNeverAssigned);
                return Results.NoContent();
            }
            catch (AccessChangeException ex)
            {
                return Results.Json(new ErrorResponse(new ErrorBody("authorization_error", "access_change_refused", ex.Message)), statusCode: 409);
            }
        }).WithTags("access");

        app.MapGet("/v1/admin/access/workspaces", async ([FromKeyedServices(AppComposition.ApplicationDatabaseKey)] NpgsqlDataSource application, CancellationToken ct) =>
        {
            await using var command = NpgsqlCatalog.Command(application, "access.workspaces");
            var rows = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new { tenantId = reader.GetString(0), displayName = reader.GetString(1), kind = reader.GetString(2), members = reader.GetInt64(3) });
            return Results.Ok(new { workspaces = rows, limit = 200 });
        }).WithTags("access");

        app.MapGet("/v1/admin/access/clients", (BrowserAuthSettings settings) => Results.Ok(new
        {
            clients = settings.Providers.Select(p => new { provider = p.Scheme, displayName = p.DisplayName,
                clientId = p.ClientId, authority = p.Authority, callbackPath = p.CallbackPath })
        })).WithTags("access");
    }
    internal sealed record PlatformRoleRequest(string? Role, bool OnlyIfNeverAssigned = false);
}
