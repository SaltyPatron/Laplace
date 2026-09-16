using System.Text.Json;
using Laplace.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat.Auth;

/// <summary>Checkout references and JSON tenant fields are not identity credentials.</summary>
internal sealed class BillingAccountBoundaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ITenantResolver resolver,
        IBillingOrchestrator billing, IStripeCheckoutGateway stripe,
        IBillingEntitlementStore entitlements, IBillingCatalog catalog,
        IOptions<StripeBillingOptions> options, IOptions<LaplaceAuthOptions> auth, BillingStoreMode store)
    {
        var path = http.Request.Path;
        if (!path.StartsWithSegments("/v1") && !path.StartsWithSegments("/chess")) { await next(http); return; }
        var tenant = await resolver.ResolveAsync(http, http.RequestAborted);
        if (tenant.AuthKind is "browser" or "api_key"
            || path.StartsWithSegments("/v1/auth") || path.StartsWithSegments("/v1/account"))
            http.Response.Headers.CacheControl = "private, no-store";
        var expectedWorkspace = http.Request.Headers["X-Laplace-Workspace"].ToString();
        if (tenant.AuthKind == "browser" && !string.IsNullOrWhiteSpace(expectedWorkspace)
            && !string.Equals(expectedWorkspace, tenant.TenantId, StringComparison.Ordinal)
            && !path.StartsWithSegments("/v1/auth") && !path.StartsWithSegments("/v1/admin")
            && !path.StartsWithSegments("/v1/billing/operator"))
        {
            await Reject(http, 409, "workspace_changed", "The selected workspace changed in another tab or session. Reload this page before continuing; this request was not executed."); return;
        }
        if (!path.StartsWithSegments("/v1/billing") || path.StartsWithSegments("/v1/billing/webhooks")
            || path.StartsWithSegments("/v1/billing/operator") || tenant.AuthKind is not ("browser" or "api_key"))
        {
            await next(http); return;
        }

        var ct = http.RequestAborted;
        if (HttpMethods.IsGet(http.Request.Method) && path.StartsWithSegments("/v1/billing/quotes", out var quotePath)
            && quotePath.Value is { Length: > 1 } suffix)
        {
            var quote = await billing.TryGetQuoteAsync(suffix[1..], ct);
            if (quote is null || !string.Equals(quote.Tenant, tenant.TenantId, StringComparison.Ordinal))
            {
                await Reject(http, 404, "quote_not_found", "No such quote exists in this workspace."); return;
            }
        }

        var subscriptionPurchase = HttpMethods.IsPost(http.Request.Method)
            && path.StartsWithSegments("/v1/billing/plans")
            && path.Value!.EndsWith("/subscribe", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasJsonContentType())
        {
            const long maxBillingBody = 1024 * 1024;
            if (http.Request.ContentLength > maxBillingBody)
            {
                await Reject(http, 413, "request_too_large", "The billing request is too large."); return;
            }
            http.Request.EnableBuffering(30 * 1024, maxBillingBody);
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await Reject(http, 400, "invalid_json", "Billing requests must be JSON objects."); return;
                }
                var supplied = UniqueProperty(root, "tenant");
                if (supplied.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    if (supplied.ValueKind != JsonValueKind.String)
                    {
                        await Reject(http, 400, "invalid_tenant", "The tenant field must be a string."); return;
                    }
                    if (!string.IsNullOrWhiteSpace(supplied.GetString())
                        && !string.Equals(supplied.GetString()!.Trim(), tenant.TenantId, StringComparison.Ordinal))
                    {
                        await Reject(http, 403, "tenant_mismatch", "The request belongs to a different workspace. Select your workspace before starting checkout."); return;
                    }
                }
                if (path.Equals(new PathString("/v1/billing/keys/redeem")))
                {
                    var id = UniqueProperty(root, "session_id");
                    if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256)
                    {
                        await Reject(http, 400, "invalid_session", "A valid checkout session ID is required."); return;
                    }
                    var session = await stripe.TryGetSessionAsync(id.GetString()!.Trim(), ct);
                    if (!session.Found || !string.Equals(session.Tenant, tenant.TenantId, StringComparison.Ordinal))
                    {
                        await Reject(http, 404, "checkout_not_found", "No such checkout exists in this workspace."); return;
                    }
                }
                if (path.Equals(new PathString("/v1/billing/preflight")))
                {
                    var service = UniqueProperty(root, "service_id");
                    if (service.ValueKind == JsonValueKind.String && catalog.ListPlans().Any(p =>
                        string.Equals(p.ServiceId, service.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        subscriptionPurchase = true;
                        var units = UniqueProperty(root, "units");
                        if (units.ValueKind != JsonValueKind.Number || !units.TryGetInt32(out var quantity) || quantity != 1)
                        {
                            await Reject(http, 400, "invalid_subscription_quantity", "Create one workspace subscription per checkout."); return;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                await Reject(http, 400, "invalid_json", "Request body must be a valid JSON object without duplicate identity fields."); return;
            }
            catch (IOException)
            {
                await Reject(http, 413, "request_too_large", "The billing request could not be buffered within its size limit."); return;
            }
            finally { http.Request.Body.Position = 0; }
        }

        if (subscriptionPurchase)
        {
            if (tenant.AuthKind == "browser"
                && (!http.Items.TryGetValue("laplace.workspace_role", out var role) || role is not ("owner" or "admin")))
            {
                await Reject(http, 403, "workspace_admin_required", "A workspace owner or administrator must manage subscriptions."); return;
            }
            if (string.IsNullOrWhiteSpace(options.Value.ApiKey) || (auth.Value.RequiresIdentity && store.Mode != "postgres"))
            {
                await Reject(http, 503, "subscription_configuration_incomplete", "Subscriptions require configured Stripe and durable PostgreSQL billing storage."); return;
            }
            var existing = await entitlements.GetByTenantAsync(tenant.TenantId, ct);
            if (existing.Any(e => !string.IsNullOrWhiteSpace(e.StripeSubscriptionId)
                && e.Status is "active" or "trialing" or "past_due" or "unpaid" or "incomplete" or "paused"))
            {
                await Reject(http, 409, "subscription_already_exists", "Manage the existing subscription from Settings instead of creating another charge."); return;
            }
        }
        await next(http);
    }

    private static JsonElement UniqueProperty(JsonElement root, string name)
    {
        JsonElement result = default;
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw new JsonException("Duplicate identity field.");
            found = true; result = property.Value;
        }
        return result;
    }
    private static Task Reject(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorBody("billing_error", code, message)));
    }
}
