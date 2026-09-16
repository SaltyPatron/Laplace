using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Options;

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
            await using (var command = NpgsqlCatalog.Command(substrate.DataSource, "account.workspaces", account.User))
            {
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
                    // Account authentication does not isolate the legacy global
                    // substrate readers. Do not infer a privacy promise from login.
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
            // Workspace and owner are admitted atomically by one catalog operation.
            await using var command = NpgsqlCatalog.Command(substrate.DataSource,
                "account.create_workspace", tenant, name, account.User);
            await command.ExecuteNonQueryAsync(ct);
            return Results.Ok(new WorkspaceView(tenant, name, "organization", "owner"));
        }).WithTags("account");

        app.MapPost("/v1/account/workspaces/{tenantId}/select", async (string tenantId, HttpContext http,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (BrowserAccount(http) is not { } account) return Error(401, "sign_in_required", "Sign in to choose a workspace.");
            await using var command = NpgsqlCatalog.Command(substrate.DataSource,
                "account.membership_role", tenantId, account.User);
            if (await command.ExecuteScalarAsync(ct) is not string role)
                return Error(403, "workspace_membership_required", "This account does not belong to that workspace.");
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
            await using var command = NpgsqlCatalog.Command(substrate.DataSource,
                "account.rename_workspace", account.Tenant, account.User, request.Name!.Trim());
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent()
                : Error(403, "workspace_admin_required", "Your workspace permissions have changed.");
        }).WithTags("account");

        app.MapGet("/v1/account/members", async (HttpContext http, SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can view the membership roster.");
            await using var command = NpgsqlCatalog.Command(substrate.DataSource, "account.members", account.Tenant);
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
            await using var command = NpgsqlCatalog.Command(substrate.DataSource, "account.create_invitation",
                id, account.Tenant, SHA256.HashData(Encoding.UTF8.GetBytes(token)), role, account.User, expires);
            if (await command.ExecuteNonQueryAsync(ct) != 1) return Error(403, "workspace_admin_required", "Your workspace permissions have changed.");
            return Results.Ok(new { invitationId = id, role, expiresAt = expires, invitationPath = $"/settings#join={token}" });
        }).WithTags("account");

        app.MapGet("/v1/account/invitations", async (HttpContext http, SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can view invitations.");
            await using var command = NpgsqlCatalog.Command(substrate.DataSource, "account.invitations", account.Tenant);
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
            await using var command = NpgsqlCatalog.Command(substrate.DataSource,
                "account.revoke_invitation", invitationId, account.Tenant, account.User);
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
            await using (var command = NpgsqlCatalog.Command(connection, transaction,
                "account.lock_invitation", SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Error(404, "invalid_invitation", "This invitation is invalid, expired, or already used.");
                id = reader.GetGuid(0); tenant = reader.GetString(1); role = reader.GetString(2);
            }
            await using (var command = NpgsqlCatalog.Command(connection, transaction,
                "account.accept_invitation", tenant, account.User, role, id))
                await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { tenantId = tenant });
        }).WithTags("account");

        app.MapDelete("/v1/account/members/{userId:guid}", async (Guid userId, HttpContext http,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            if (Manager(http) is not { } account) return Error(403, "workspace_admin_required", "Only workspace administrators can remove members.");
            await using var connection = await substrate.DataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var command = NpgsqlCatalog.Command(connection, transaction, "account.lock_workspace", account.Tenant))
                await command.ExecuteScalarAsync(ct);
            await using (var remove = NpgsqlCatalog.Command(connection, transaction,
                "account.remove_member", account.Tenant, userId, account.User))
            {
                if (await remove.ExecuteNonQueryAsync(ct) != 1)
                    return Error(409, "member_not_removable", "The member is absent, your role cannot remove them, or they are the last owner.");
            }
            await using (var revoke = NpgsqlCatalog.Command(connection, transaction,
                "identity.revoke_workspace_sessions", account.Tenant, userId))
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
            var now = DateTimeOffset.UtcNow;
            if (!plans.Any(p => p.Status == "active" && p.PeriodStart <= now && p.PeriodEnd > now))
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
            var now = DateTimeOffset.UtcNow;
            var active = subscriptions.Any(e => e.StripeSubscriptionId == session.SubscriptionId
                && e.Status == "active" && e.PeriodStart <= now && e.PeriodEnd > now);
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
