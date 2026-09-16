using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/account", async (HttpContext http, SubstrateClient substrate,
            BrowserAuthSettings identity, IOptions<LaplaceAuthOptions> auth,
            IOptions<StripeBillingOptions> stripe, BillingStoreMode store,
            IBillingEntitlementStore entitlements, CancellationToken ct) =>
        {
            if (BrowserAccount(http) is not { } account) return Error(401, "sign_in_required", "Sign in to manage your account.");
            var workspaces = new List<WorkspaceView>();
            await using (var command = substrate.DataSource.CreateCommand("""
                SELECT t.tenant_id, t.display_name, t.kind, m.role
                FROM app.tenant_memberships m JOIN app.tenants t USING (tenant_id)
                WHERE m.user_id = @user ORDER BY t.display_name, t.tenant_id;
                """))
            {
                command.Parameters.AddWithValue("user", account.User);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    workspaces.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            return Results.Ok(new
            {
                userId = account.User, tenantId = account.Tenant,
                displayName = http.User.FindFirstValue(ClaimTypes.Name),
                email = http.User.FindFirstValue(ClaimTypes.Email),
                provider = http.User.FindFirstValue(LaplaceClaimTypes.Provider),
                workspaces,
                subscriptions = await entitlements.GetByTenantAsync(account.Tenant, ct),
                configuration = new
                {
                    authMode = auth.Value.Mode,
                    billingEnforced = !stripe.Value.Bypass,
                    billingStore = store.Mode,
                    stripeConfigured = !string.IsNullOrWhiteSpace(stripe.Value.ApiKey),
                    publicBaseUrl = stripe.Value.PublicBaseUrl,
                    persistentSessionKeys = !string.IsNullOrWhiteSpace(LaplaceInstall.TryReadConfig("LAPLACE_DATA_PROTECTION_KEYS", "identity.env")),
                    providers = identity.Providers.Select(p => new { id = p.Scheme, name = p.DisplayName, callbackPath = p.CallbackPath }),
                    // This reports the actual legacy data-reader contract, not a
                    // privacy promise inferred from authentication being configured.
                    substrateScope = "shared", privateDataIsolation = false
                }
            });
        }).WithTags("account");

        app.MapPost("/v1/account/workspaces", async (HttpContext http, WorkspaceNameRequest request,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (BrowserAccount(http) is not { } account) return Error(401, "sign_in_required", "Sign in to create a company workspace.");
            if (!ValidName(request.Name)) return Error(400, "invalid_name", "Use a workspace name between 1 and 160 characters without control characters.");
            var tenant = $"t-{Guid.NewGuid():N}";
            var name = request.Name!.Trim();
            await using var connection = await substrate.DataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("""
                INSERT INTO app.tenants (tenant_id, display_name, kind) VALUES (@tenant, @name, 'organization');
                INSERT INTO app.tenant_memberships (tenant_id, user_id, role) VALUES (@tenant, @user, 'owner');
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("user", account.User);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new WorkspaceView(tenant, name, "organization", "owner"));
        }).WithTags("account");

        app.MapPost("/v1/account/workspaces/{tenantId}/select", async (string tenantId, HttpContext http,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (BrowserAccount(http) is not { } account) return Error(401, "sign_in_required", "Sign in to select a workspace.");
            await using var command = substrate.DataSource.CreateCommand("""
                SELECT role FROM app.tenant_memberships WHERE tenant_id = @tenant AND user_id = @user;
                """);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("user", account.User);
            if (await command.ExecuteScalarAsync(ct) is not string role)
                return Error(403, "workspace_membership_required", "This account does not belong to that workspace.");
            // An opaque login session and a workspace selection have different
            // lifetimes. The resolver revalidates membership on every request.
            var cookie = ApiKeyTenantResolver.WorkspaceCookieOptions();
            cookie.Expires = DateTimeOffset.UtcNow.AddDays(14);
            http.Response.Cookies.Append(ApiKeyTenantResolver.WorkspaceCookie, tenantId, cookie);
            return Results.Ok(new { tenantId, role });
        }).WithTags("account");

        app.MapPut("/v1/account/workspace", async (HttpContext http, WorkspaceNameRequest request,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "A workspace owner or administrator must change its settings.");
            if (!ValidName(request.Name)) return Error(400, "invalid_name", "Use a workspace name between 1 and 160 characters without control characters.");
            await using var command = substrate.DataSource.CreateCommand("""
                UPDATE app.tenants SET display_name = @name, updated_at = now()
                WHERE tenant_id = @tenant AND EXISTS (
                    SELECT 1 FROM app.tenant_memberships
                    WHERE tenant_id = @tenant AND user_id = @user AND role IN ('owner', 'admin'));
                """);
            command.Parameters.AddWithValue("tenant", account.Tenant);
            command.Parameters.AddWithValue("user", account.User);
            command.Parameters.AddWithValue("name", request.Name!.Trim());
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent()
                : Error(403, "workspace_admin_required", "Your workspace permissions have changed.");
        }).WithTags("account");

        app.MapGet("/v1/account/members", async (HttpContext http, SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can view the membership roster.");
            await using var command = substrate.DataSource.CreateCommand("""
                SELECT u.user_id, u.display_name, u.email, m.role
                FROM app.tenant_memberships m JOIN app.users u USING (user_id)
                WHERE m.tenant_id = @tenant ORDER BY m.created_at, u.user_id;
                """);
            command.Parameters.AddWithValue("tenant", account.Tenant);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var members = new List<object>();
            while (await reader.ReadAsync(ct)) members.Add(new
            {
                userId = reader.GetGuid(0), displayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                email = reader.IsDBNull(2) ? null : reader.GetString(2), role = reader.GetString(3)
            });
            return Results.Ok(new { members });
        }).WithTags("account");

        app.MapPost("/v1/account/invitations", async (HttpContext http, InvitationRequest request,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can invite members.");
            var role = request.Role ?? "member";
            if (role is not ("member" or "admin") || (role == "admin" && Role(http) != "owner"))
                return Error(403, "invalid_invitation_role", "Administrators can invite members; only owners can invite administrators.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var id = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.AddDays(7);
            await using var command = substrate.DataSource.CreateCommand("""
                INSERT INTO app.workspace_invitations
                    (invitation_id, tenant_id, token_hash, role, created_by, expires_at)
                SELECT @id, @tenant, @hash, @role, @user, @expires
                WHERE EXISTS (SELECT 1 FROM app.tenant_memberships
                    WHERE tenant_id = @tenant AND user_id = @user
                      AND (role = 'owner' OR (role = 'admin' AND @role = 'member')));
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("tenant", account.Tenant);
            command.Parameters.AddWithValue("hash", SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("user", account.User);
            command.Parameters.AddWithValue("expires", expires);
            if (await command.ExecuteNonQueryAsync(ct) != 1) return Error(403, "workspace_admin_required", "Your workspace permissions have changed.");
            return Results.Ok(new { invitationId = id, role, expiresAt = expires, invitationPath = $"/settings#join={token}" });
        }).WithTags("account");

        app.MapGet("/v1/account/invitations", async (HttpContext http, SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can view invitations.");
            await using var command = substrate.DataSource.CreateCommand("""
                SELECT invitation_id, role, created_at, expires_at FROM app.workspace_invitations
                WHERE tenant_id = @tenant AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > now()
                ORDER BY created_at DESC;
                """);
            command.Parameters.AddWithValue("tenant", account.Tenant);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var invitations = new List<object>();
            while (await reader.ReadAsync(ct)) invitations.Add(new
            {
                invitationId = reader.GetGuid(0), role = reader.GetString(1),
                createdAt = reader.GetFieldValue<DateTimeOffset>(2), expiresAt = reader.GetFieldValue<DateTimeOffset>(3)
            });
            return Results.Ok(new { invitations });
        }).WithTags("account");

        app.MapDelete("/v1/account/invitations/{invitationId:guid}", async (Guid invitationId, HttpContext http,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can revoke invitations.");
            await using var command = substrate.DataSource.CreateCommand("""
                UPDATE app.workspace_invitations SET revoked_at = now()
                WHERE invitation_id = @id AND tenant_id = @tenant AND accepted_at IS NULL AND revoked_at IS NULL;
                """);
            command.Parameters.AddWithValue("id", invitationId);
            command.Parameters.AddWithValue("tenant", account.Tenant);
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.NotFound();
        }).WithTags("account");

        app.MapPost("/v1/account/invitations/accept", async (HttpContext http, AcceptInvitationRequest request,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (BrowserAccount(http) is not { } account) return Error(401, "sign_in_required", "Sign in before accepting an invitation.");
            if (request.Token is not { Length: 64 } || !request.Token.All(Uri.IsHexDigit))
                return Error(400, "invalid_invitation", "This invitation is invalid or expired.");
            await using var connection = await substrate.DataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            string tenant;
            string role;
            Guid id;
            await using (var command = new NpgsqlCommand("""
                SELECT i.invitation_id, i.tenant_id, i.role
                FROM app.workspace_invitations i
                JOIN app.tenant_memberships m ON m.tenant_id = i.tenant_id AND m.user_id = i.created_by
                WHERE i.token_hash = @hash AND i.expires_at > now()
                  AND i.revoked_at IS NULL AND i.accepted_at IS NULL
                  AND (m.role = 'owner' OR (m.role = 'admin' AND i.role = 'member'))
                FOR UPDATE OF i;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("hash", SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Error(404, "invalid_invitation", "This invitation is invalid, expired, or already used.");
                id = reader.GetGuid(0); tenant = reader.GetString(1); role = reader.GetString(2);
            }
            await using (var command = new NpgsqlCommand("""
                INSERT INTO app.tenant_memberships (tenant_id, user_id, role)
                VALUES (@tenant, @user, @role) ON CONFLICT (tenant_id, user_id) DO NOTHING;
                UPDATE app.workspace_invitations SET accepted_at = now(), accepted_by = @user WHERE invitation_id = @id;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("tenant", tenant);
                command.Parameters.AddWithValue("user", account.User);
                command.Parameters.AddWithValue("role", role);
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { tenantId = tenant });
        }).WithTags("account");

        app.MapDelete("/v1/account/members/{userId:guid}", async (Guid userId, HttpContext http,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can remove members.");
            await using var connection = await substrate.DataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var command = new NpgsqlCommand("SELECT tenant_id FROM app.tenants WHERE tenant_id = @tenant FOR UPDATE", connection, transaction))
            {
                command.Parameters.AddWithValue("tenant", account.Tenant);
                await command.ExecuteScalarAsync(ct);
            }
            await using var remove = new NpgsqlCommand("""
                DELETE FROM app.tenant_memberships target
                WHERE target.tenant_id = @tenant AND target.user_id = @target
                  AND EXISTS (SELECT 1 FROM app.tenant_memberships actor
                    WHERE actor.tenant_id = @tenant AND actor.user_id = @actor
                      AND (actor.role = 'owner' OR (actor.role = 'admin' AND target.role = 'member')))
                  AND (target.role <> 'owner' OR
                    (SELECT count(*) FROM app.tenant_memberships WHERE tenant_id = @tenant AND role = 'owner') > 1);
                """, connection, transaction);
            remove.Parameters.AddWithValue("tenant", account.Tenant);
            remove.Parameters.AddWithValue("target", userId);
            remove.Parameters.AddWithValue("actor", account.User);
            if (await remove.ExecuteNonQueryAsync(ct) != 1)
                return Error(409, "member_not_removable", "The member is absent, your role cannot remove them, or they are the last owner.");
            await using var revoke = new NpgsqlCommand("""
                UPDATE app.web_sessions SET revoked_at = now()
                WHERE tenant_id = @tenant AND user_id = @user AND revoked_at IS NULL;
                """, connection, transaction);
            revoke.Parameters.AddWithValue("tenant", account.Tenant);
            revoke.Parameters.AddWithValue("user", userId);
            await revoke.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).WithTags("account");

        app.MapPost("/v1/billing/keys", async (HttpContext http, NewKeyRequest request,
            IApiKeyService keys, IBillingEntitlementStore entitlements, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Sign in as a workspace owner or administrator to create API keys.");
            if (request.Label?.Length > 160) return Error(400, "invalid_label", "Key labels may contain at most 160 characters.");
            var plans = await entitlements.GetByTenantAsync(account.Tenant, ct);
            if (!plans.Any(p => p.Status == "active" && p.PeriodStart <= DateTimeOffset.UtcNow && p.PeriodEnd > DateTimeOffset.UtcNow))
                return Error(402, "active_subscription_required", "An active workspace subscription is required to create an API key.");
            var issued = await keys.IssueAsync(account.Tenant, request.Label?.Trim(), ct);
            return Results.Ok(new { key = issued.Key, keyPrefix = issued.Record.KeyPrefix,
                tenantId = account.Tenant, message = "Save this secret now. It is shown only once." });
        }).WithTags("billing");

        app.MapPost("/v1/billing/checkout/status", async (HttpContext http, CheckoutStatusRequest request,
            IStripeCheckoutGateway gateway, IBillingEntitlementStore entitlements, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Sign in as a workspace administrator to view checkout.");
            if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256)
                return Error(400, "invalid_session", "A checkout session ID is required.");
            var session = await gateway.TryGetSessionAsync(request.SessionId, ct);
            if (!session.Found || !string.Equals(session.Tenant, account.Tenant, StringComparison.Ordinal)) return Results.NotFound();
            var subscriptions = await entitlements.GetByTenantAsync(account.Tenant, ct);
            var active = subscriptions.Any(e => e.StripeSubscriptionId == session.SubscriptionId
                && e.Status == "active" && e.PeriodStart <= DateTimeOffset.UtcNow && e.PeriodEnd > DateTimeOffset.UtcNow);
            return Results.Ok(new { paid = session.Paid, active, tenantId = account.Tenant,
                status = active ? "active" : session.Paid ? "awaiting_subscription_confirmation" : "awaiting_payment" });
        }).WithTags("billing");

        app.MapPost("/v1/billing/portal", async (HttpContext http, IBillingEntitlementStore entitlements,
            IOptions<StripeBillingOptions> options, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Sign in as a workspace owner or administrator to manage billing.");
            if (string.IsNullOrWhiteSpace(options.Value.ApiKey)) return Error(503, "stripe_not_configured", "Billing is not configured on this host.");
            if (!Uri.TryCreate(options.Value.PublicBaseUrl, UriKind.Absolute, out var origin)
                || origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo)
                || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                return Error(503, "billing_return_url_unconfigured", "The operator must configure LAPLACE_PUBLIC_BASE_URL as the public HTTPS address.");
            var customer = (await entitlements.GetByTenantAsync(account.Tenant, ct))
                .OrderByDescending(e => e.UpdatedAt).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.StripeCustomerId))?.StripeCustomerId;
            if (customer is null) return Error(409, "billing_customer_missing", "Complete a subscription checkout before opening billing management.");
            try
            {
                var service = new Stripe.BillingPortal.SessionService(new Stripe.StripeClient(options.Value.ApiKey));
                var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customer, ReturnUrl = new Uri(origin, "/settings").AbsoluteUri
                }, cancellationToken: ct);
                return Results.Ok(new { url = session.Url });
            }
            catch (Stripe.StripeException)
            {
                return Error(503, "billing_portal_unavailable", "Stripe billing management is unavailable. The operator must check the Stripe customer-portal configuration.");
            }
        }).WithTags("billing");
    }

    private static (Guid User, string Tenant)? BrowserAccount(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true || ApiKeyTenantResolver.PresentedKey(http.Request) is not null) return null;
        var tenant = http.User.FindFirstValue(LaplaceClaimTypes.Tenant);
        return Guid.TryParse(http.User.FindFirstValue(LaplaceClaimTypes.User), out var user)
            && !string.IsNullOrWhiteSpace(tenant) ? (user, tenant) : null;
    }
    private static string? Role(HttpContext http) => http.Items.TryGetValue("laplace.workspace_role", out var role) ? role as string : null;
    private static (Guid User, string Tenant)? Manager(HttpContext http) => Role(http) is "owner" or "admin" ? BrowserAccount(http) : null;
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 160 && !name.Any(char.IsControl);
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(new ErrorBody("account_error", code, message)), statusCode: status);

    internal sealed record WorkspaceView(string TenantId, string DisplayName, string Kind, string Role);
    internal sealed record WorkspaceNameRequest(string? Name);
    internal sealed record InvitationRequest(string? Role);
    internal sealed record AcceptInvitationRequest(string? Token);
    internal sealed record NewKeyRequest(string? Label);
    internal sealed record CheckoutStatusRequest(string? SessionId);
}
