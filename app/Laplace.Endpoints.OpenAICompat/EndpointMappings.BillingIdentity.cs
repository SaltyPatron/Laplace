using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Identity + operator halves of billing: self-serve API-key redemption after
/// checkout, key management for an authenticated tenant, and the operator-token
/// surface (manual quote approval, key issuance, bootstrap re-run).
/// </summary>
internal static class BillingIdentityEndpoints
{
    public static void MapBillingIdentityEndpoints(this WebApplication app)
    {
        // Self-serve: turn a PAID plan checkout session into an API key. The session id
        // arrives on the Stripe success redirect (?session_id=...), so possession of it
        // proves the payment; the key binds to the tenant recorded in session metadata.
        app.MapPost("/v1/billing/keys/redeem", async (
            HttpRequest request,
            IStripeCheckoutGateway stripe,
            IBillingCatalog catalog,
            IApiKeyService apiKeys,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ApiKeyRedeemRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'session_id' is required.");

            var sessionId = payload.SessionId.Trim();
            var session = await stripe.TryGetSessionAsync(sessionId, ct);
            if (!session.Found)
                return Results.Json(new ApiKeyRedeemResponse(false, "session_not_found", null, null, null, null,
                    "Stripe session was not found (or Stripe is not configured)."),
                    statusCode: StatusCodes.Status404NotFound);
            if (!session.Paid)
                return Results.Json(new ApiKeyRedeemResponse(false, "not_paid", session.Tenant, null, null, null,
                    "Checkout session is not paid yet."), statusCode: StatusCodes.Status402PaymentRequired);
            if (string.IsNullOrWhiteSpace(session.Tenant))
                return Results.Json(new ApiKeyRedeemResponse(false, "no_tenant", null, null, null, null,
                    "Session carries no tenant metadata; contact the operator."),
                    statusCode: StatusCodes.Status409Conflict);

            var plan = catalog.ListPlans().FirstOrDefault(p =>
                string.Equals(p.ServiceId, session.ServiceId, StringComparison.OrdinalIgnoreCase));
            if (plan is null)
                return Results.Json(new ApiKeyRedeemResponse(false, "not_a_plan", session.Tenant, null, null, null,
                    "Only plan subscriptions issue API keys; one-off quotes execute via X-Laplace-Quote-Id."),
                    statusCode: StatusCodes.Status409Conflict);

            var sessionLabel = $"session:{sessionId}";
            if ((await apiKeys.FindByLabelAsync(sessionLabel, ct)).Count > 0)
                return Results.Json(new ApiKeyRedeemResponse(false, "already_redeemed", session.Tenant, plan.PlanId, null, null,
                    "This checkout session already issued a key. Keys are shown once; ask the operator to issue a replacement if it was lost."),
                    statusCode: StatusCodes.Status409Conflict);

            var issued = await apiKeys.IssueAsync(session.Tenant, sessionLabel, ct);
            return Results.Json(new ApiKeyRedeemResponse(true, "issued", session.Tenant, plan.PlanId,
                issued.Key, issued.Record.KeyPrefix,
                "Store this key now — it is shown exactly once. Send it as 'Authorization: Bearer <key>'."));
        })
        .WithTags("billing")
        .Accepts<ApiKeyRedeemRequest>("application/json")
        .Produces<ApiKeyRedeemResponse>()
        .Produces<ApiKeyRedeemResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ApiKeyRedeemResponse>(StatusCodes.Status404NotFound)
        .Produces<ApiKeyRedeemResponse>(StatusCodes.Status409Conflict);

        // Authenticated tenant: list / revoke own keys (never returns the secret).
        app.MapGet("/v1/billing/keys", async (
            HttpRequest request, IApiKeyService apiKeys, ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var keys = await apiKeys.ListAsync(tenant, ct);
            return Results.Json(new ApiKeyListResponse(tenant, keys.Select(View).ToArray()));
        })
        .WithTags("billing").Produces<ApiKeyListResponse>();

        app.MapPost("/v1/billing/keys/revoke", async (
            HttpRequest request, IApiKeyService apiKeys, ITenantResolver resolver, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ApiKeyRevokeRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.KeyPrefix))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'key_prefix' is required.");

            var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
            var revoked = await apiKeys.RevokeByPrefixAsync(tenant, payload.KeyPrefix.Trim(), ct);
            return revoked
                ? Results.Json(new { revoked = true, key_prefix = payload.KeyPrefix.Trim() })
                : EndpointJson.BadRequest("key_not_found", "No active key with that prefix for this tenant.");
        })
        .WithTags("billing")
        .Accepts<ApiKeyRevokeRequest>("application/json");

        // ---- Operator surface (X-Laplace-Operator-Token) --------------------------

        app.MapPost("/v1/billing/operator/quotes/{quoteId}/approve", async (
            string quoteId,
            HttpRequest request,
            IBillingOrchestrator billing,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            if (!OperatorAuth.IsAuthorized(request, auth.Value))
                return OperatorForbidden();

            var approved = await billing.TryApproveQuoteAsync(quoteId, ct);
            return approved is null
                ? EndpointJson.BadRequest("quote_not_found", "Quote does not exist.")
                : Results.Json(new OperatorApproveResponse(true, approved.QuoteId, approved.Status));
        })
        .WithTags("billing").Produces<OperatorApproveResponse>();

        app.MapPost("/v1/billing/operator/keys", async (
            HttpRequest request,
            IApiKeyService apiKeys,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            if (!OperatorAuth.IsAuthorized(request, auth.Value))
                return OperatorForbidden();

            var payload = await EndpointJson.ReadJsonAsync<ApiKeyIssueRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Tenant))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'tenant' is required.");

            var issued = await apiKeys.IssueAsync(payload.Tenant.Trim(), payload.Label, ct);
            return Results.Json(new ApiKeyIssueResponse(
                issued.Key, issued.Record.KeyPrefix, issued.Record.Tenant, issued.Record.Label,
                "Store this key now — it is shown exactly once."));
        })
        .WithTags("billing")
        .Accepts<ApiKeyIssueRequest>("application/json")
        .Produces<ApiKeyIssueResponse>();

        app.MapPost("/v1/billing/operator/bootstrap", async (
            HttpRequest request,
            IBillingBootstrap bootstrap,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            if (!OperatorAuth.IsAuthorized(request, auth.Value))
                return OperatorForbidden();

            var result = await bootstrap.RunAsync(ct);
            return Results.Json(new BillingBootstrapResponse(
                StoreMode: result.StoreMode,
                StripeConfigured: result.StripeConfigured,
                BillingEnforced: result.BillingEnforced,
                Catalog: new CatalogSyncResponse(
                    result.Catalog.StripeConfigured,
                    result.Catalog.Entries.Select(e => new CatalogSyncEntryView(
                        e.ServiceId, e.LookupKey, e.StripePriceId, e.StripeProductId, e.Status)).ToArray()),
                Webhook: new WebhookProvisionView(
                    result.Webhook.Status, result.Webhook.EndpointId, result.Webhook.Url)));
        })
        .WithTags("billing").Produces<BillingBootstrapResponse>();
    }

    private static ApiKeyView View(ApiKeyRecord record) => new(
        record.KeyPrefix, record.Tenant, record.Label, record.CreatedAt, record.RevokedAt, record.LastUsedAt);

    private static IResult OperatorForbidden() =>
        Results.Json(new ErrorResponse(new ErrorBody(
            "authentication_error", "operator_token_required",
            "Set LAPLACE_OPERATOR_TOKEN on the server and send it as X-Laplace-Operator-Token.")),
            statusCode: StatusCodes.Status403Forbidden);
}
