using System.Text.Json;
using Laplace.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat.Auth;

/// <summary>
/// Customer identity is the billing boundary. A checkout reference or JSON tenant
/// field is not an authorization credential, even when Stripe says it was paid.
/// </summary>
internal sealed class BillingAccountBoundaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ITenantResolver resolver,
        IBillingOrchestrator billing, IStripeCheckoutGateway stripe,
        IBillingEntitlementStore entitlements, IOptions<StripeBillingOptions> options,
        IOptions<LaplaceAuthOptions> auth, BillingStoreMode store)
    {
        var path = http.Request.Path;
        if (!path.StartsWithSegments("/v1")) { await next(http); return; }
        var tenant = await resolver.ResolveAsync(http, http.RequestAborted);
        if (tenant.AuthKind is "browser" or "api_key")
            http.Response.Headers.CacheControl = "private, no-store";

        if (!path.StartsWithSegments("/v1/billing")
            || path.StartsWithSegments("/v1/billing/webhooks")
            || path.StartsWithSegments("/v1/billing/operator")
            || tenant.AuthKind is not ("browser" or "api_key"))
        {
            await next(http);
            return;
        }

        var ct = http.RequestAborted;
        if (HttpMethods.IsGet(http.Request.Method)
            && path.StartsWithSegments("/v1/billing/quotes", out var quotePath)
            && quotePath.Value is { Length: > 1 } suffix)
        {
            var quote = await billing.TryGetQuoteAsync(suffix[1..], ct);
            if (quote is null || !string.Equals(quote.Tenant, tenant.TenantId, StringComparison.Ordinal))
            {
                await Reject(http, 404, "quote_not_found", "No such quote exists in this workspace.");
                return;
            }
        }

        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasJsonContentType())
        {
            const long maxBillingBody = 1024 * 1024;
            if (http.Request.ContentLength > maxBillingBody)
            {
                await Reject(http, 413, "request_too_large", "The billing request is too large.");
                return;
            }
            http.Request.EnableBuffering(30 * 1024, maxBillingBody);
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await Reject(http, 400, "invalid_json", "Billing requests must be JSON objects.");
                    return;
                }
                if (root.TryGetProperty("tenant", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
                {
                    if (supplied.ValueKind != JsonValueKind.String)
                    {
                        await Reject(http, 400, "invalid_tenant", "The tenant field must be a string.");
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(supplied.GetString())
                        && !string.Equals(supplied.GetString()!.Trim(), tenant.TenantId, StringComparison.Ordinal))
                    {
                        await Reject(http, 403, "tenant_mismatch", "The request belongs to a different workspace. Select your workspace before starting checkout.");
                        return;
                    }
                }
                if (path.Equals(new PathString("/v1/billing/keys/redeem"))
                    && root.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    var session = await stripe.TryGetSessionAsync(id.GetString()!.Trim(), ct);
                    if (!session.Found || !string.Equals(session.Tenant, tenant.TenantId, StringComparison.Ordinal))
                    {
                        await Reject(http, 404, "checkout_not_found", "No such checkout exists in this workspace.");
                        return;
                    }
                }
            }
            catch (JsonException)
            {
                await Reject(http, 400, "invalid_json", "Request body must be valid JSON.");
                return;
            }
            catch (IOException)
            {
                await Reject(http, 413, "request_too_large", "The billing request could not be buffered within its size limit.");
                return;
            }
            finally { http.Request.Body.Position = 0; }
        }

        if (HttpMethods.IsPost(http.Request.Method)
            && path.StartsWithSegments("/v1/billing/plans")
            && path.Value!.EndsWith("/subscribe", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Value.ApiKey)
                || (auth.Value.RequiresIdentity && store.Mode != "postgres"))
            {
                await Reject(http, 503, "subscription_configuration_incomplete", "Subscriptions require configured Stripe and durable PostgreSQL billing storage.");
                return;
            }
            var existing = await entitlements.GetByTenantAsync(tenant.TenantId, ct);
            if (existing.Any(e => !string.IsNullOrWhiteSpace(e.StripeSubscriptionId)
                && e.Status is "active" or "trialing" or "past_due" or "unpaid" or "incomplete" or "paused"))
            {
                await Reject(http, 409, "subscription_already_exists", "Manage the existing subscription from Settings instead of creating another charge.");
                return;
            }
        }
        await next(http);
    }

    private static Task Reject(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorBody("billing_error", code, message)));
    }
}
