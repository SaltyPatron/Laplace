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
/// In key mode, /v1/* and /chess/* require a valid API key except for the anonymous
/// /v1 surface a not-yet-customer needs to sign up: discovery, billing
/// catalog/plans/preflight, checkout redemption, and Stripe webhooks. Header mode
/// enforces nothing. /chess/* has no anonymous prefixes — GH #489 / C04.
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

    /// <summary>
    /// True when <paramref name="path"/> IS <paramref name="segment"/> or sits beneath it.
    /// "/v1" and "/v1/models" match "/v1"; "/v10" and "/v1x" do not.
    /// </summary>
    private static bool IsUnder(string path, string segment) =>
        path.Equals(segment, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
            && path.Length > segment.Length
            && path[segment.Length] == '/');

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        var path = context.Request.Path.Value ?? "";
        // Host lifecycle uses the operator credential, not a customer billing key
        // and never the permissive tenant-header mode. Keep the legacy aliases in
        // the same fail-closed policy so they cannot start a second bot.
        if (IsUnder(path, "/v1/admin/services")
            || path.Equals("/chess/lichess/start", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/chess/lichess/stop", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatorAuth.IsAuthorized(context.Request, _options))
            {
                context.RequestServices.GetRequiredService<ILogger<ApiKeyEnforcementMiddleware>>()
                    .LogWarning("service control authorization denied: method={Method} path={Path}", context.Request.Method, path);
                await Reject(context, "operator_token_required", "This endpoint requires the operator credential.");
                return;
            }
            await _next(context);
            return;
        }
        // Playing surface sits at /chess/* (outside /v1); without this branch key mode
        // never sees it and C04 stays open (GH #489).
        //
        // Match on a SEGMENT boundary, not a bare prefix. StartsWith("/v1") also matched
        // /v10/*, and StartsWith("/chess") also matched /chessboard/* — this decides
        // whether tenant governance applies at all, so a prefix collision is an
        // authorization-surface bug, not a routing nicety. No such route exists today,
        // which is exactly why nobody would notice when one is added.
        var governed = IsUnder(path, "/v1") || IsUnder(path, "/chess");
        if (!governed)
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

    /// <summary>
    /// Constant-time check of the operator token. Chess Lab is an interactive local-dev
    /// work surface, so header-mode hosts do not require a second secret that the product
    /// has no discovery/provisioning path for. Key-mode/production hosts remain fail-closed.
    /// Service-control and billing operator endpoints retain the token requirement in all modes.
    /// </summary>
    public static bool IsAuthorized(HttpRequest request, LaplaceAuthOptions options)
    {
        if (!options.KeyMode && request.Path.StartsWithSegments("/chess/lab"))
            return true;
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