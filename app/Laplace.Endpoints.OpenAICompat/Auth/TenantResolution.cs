using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat.Auth;

public sealed record TenantContext(
    string TenantId,
    string AuthKind,
    IReadOnlyDictionary<string, string> Claims)
{
    public static readonly IReadOnlyDictionary<string, string> NoClaims =
        new Dictionary<string, string>();
}

public interface ITenantResolver
{
    ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct);
}

internal sealed class LaplaceAuthOptions
{
    /// <summary>"header" trusts X-Laplace-Tenant (local/dev); "key" requires a valid API key on /v1/*.</summary>
    public string Mode { get; set; } = "header";

    /// <summary>Shared secret for operator endpoints (quote approval, key issuance, bootstrap).</summary>
    public string? OperatorToken { get; set; }

    public bool KeyMode => string.Equals(Mode, "key", StringComparison.OrdinalIgnoreCase);
}

internal sealed class HeaderTenantResolver : ITenantResolver
{
    public const string TenantHeader = "X-Laplace-Tenant";
    public const string DefaultTenant = "local-dev";

    public ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        var header = context.Request.Headers[TenantHeader].ToString();
        var tenant = string.IsNullOrWhiteSpace(header) ? DefaultTenant : header.Trim();
        return ValueTask.FromResult(new TenantContext(tenant, "header", TenantContext.NoClaims));
    }
}

/// <summary>
/// Resolves an API key (Authorization: Bearer sk-laplace-… or X-Api-Key) to its tenant;
/// falls back to header tenancy when no key is presented. A presented-but-invalid key
/// resolves to AuthKind "invalid_key" so the middleware can reject it, never to a
/// fallback tenant. The result is cached per request in HttpContext.Items.
/// </summary>
internal sealed class ApiKeyTenantResolver : ITenantResolver
{
    private const string CacheKey = "laplace.tenant_context";
    private readonly IApiKeyService _apiKeys;
    private readonly HeaderTenantResolver _header = new();

    public ApiKeyTenantResolver(IApiKeyService apiKeys) => _apiKeys = apiKeys;

    public async ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Items.TryGetValue(CacheKey, out var cached) && cached is TenantContext hit)
            return hit;

        var resolved = await ResolveUncachedAsync(context, ct);
        context.Items[CacheKey] = resolved;
        return resolved;
    }

    private async ValueTask<TenantContext> ResolveUncachedAsync(HttpContext context, CancellationToken ct)
    {
        var presented = PresentedKey(context.Request);
        if (presented is null)
            return await _header.ResolveAsync(context, ct);

        var record = await _apiKeys.ValidateAsync(presented, ct);
        if (record is null)
            return new TenantContext("", "invalid_key", TenantContext.NoClaims);

        return new TenantContext(record.Tenant, "api_key",
            new Dictionary<string, string> { ["key_prefix"] = record.KeyPrefix });
    }

    public static string? PresentedKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (token.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal))
                return token;
        }

        var headerKey = request.Headers["X-Api-Key"].ToString().Trim();
        return headerKey.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal) ? headerKey : null;
    }
}

/// <summary>
/// In key mode, /v1/* requires a valid API key except for the anonymous surface a
/// not-yet-customer needs to sign up: discovery, billing catalog/plans/preflight,
/// checkout redemption, and Stripe webhooks. Header mode enforces nothing.
/// </summary>
internal sealed class ApiKeyEnforcementMiddleware
{
    private static readonly string[] AnonymousPrefixes =
    {
        "/v1/models",
        "/v1/capabilities",
        "/v1/billing/catalog",
        "/v1/billing/products",
        "/v1/billing/plans",
        "/v1/billing/preflight",
        "/v1/billing/quotes",
        "/v1/billing/keys/redeem",
        "/v1/billing/webhooks",
        "/v1/billing/operator"
    };

    private readonly RequestDelegate _next;
    private readonly LaplaceAuthOptions _options;

    public ApiKeyEnforcementMiddleware(RequestDelegate next, IOptions<LaplaceAuthOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var tenant = await resolver.ResolveAsync(context, context.RequestAborted);
        if (string.Equals(tenant.AuthKind, "invalid_key", StringComparison.Ordinal))
        {
            await Reject(context, "invalid_api_key", "The provided API key is unknown or revoked.");
            return;
        }

        if (_options.KeyMode &&
            !string.Equals(tenant.AuthKind, "api_key", StringComparison.Ordinal) &&
            !AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await Reject(context, "api_key_required",
                "This endpoint requires an API key. Subscribe to a plan and redeem your checkout session at POST /v1/billing/keys/redeem.");
            return;
        }

        await _next(context);
    }

    private static Task Reject(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new Laplace.Api.Contracts.ErrorResponse(
            new Laplace.Api.Contracts.ErrorBody("authentication_error", code, message)));
    }
}

internal static class OperatorAuth
{
    public const string TokenHeader = "X-Laplace-Operator-Token";

    /// <summary>Constant-time check of the operator token; false when unconfigured.</summary>
    public static bool IsAuthorized(HttpRequest request, LaplaceAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OperatorToken))
            return false;
        var presented = request.Headers[TokenHeader].ToString();
        if (string.IsNullOrWhiteSpace(presented))
            return false;
        var a = System.Text.Encoding.UTF8.GetBytes(presented);
        var b = System.Text.Encoding.UTF8.GetBytes(options.OperatorToken);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
