using System.Security.Claims;
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
    public string Mode { get; set; } = "header";
    public string? OperatorToken { get; set; }
    public bool KeyMode => string.Equals(Mode, "key", StringComparison.OrdinalIgnoreCase);
    public bool IdentityMode => string.Equals(Mode, "identity", StringComparison.OrdinalIgnoreCase);
    // Only the explicitly selected development mode trusts headers. A typo must
    // never turn a public deployment into an anonymous tenant selector.
    public bool RequiresIdentity => !string.Equals(Mode, "header", StringComparison.OrdinalIgnoreCase);
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
        {
            // Explicit credentials must not silently fall back to an unrelated
            // browser account or caller-controlled development tenant.
            if (context.Request.Headers.ContainsKey("Authorization")
                || context.Request.Headers.ContainsKey("X-Api-Key"))
                return new TenantContext("", "invalid_key", TenantContext.NoClaims);

            var principal = context.User;
            var tenant = principal.FindFirstValue(LaplaceClaimTypes.Tenant);
            var user = principal.FindFirstValue(LaplaceClaimTypes.User);
            if (principal.Identity?.IsAuthenticated == true
                && !string.IsNullOrWhiteSpace(tenant)
                && Guid.TryParse(user, out _))
                return new TenantContext(tenant, "browser", new Dictionary<string, string>
                {
                    ["user_id"] = user!,
                    ["provider"] = principal.FindFirstValue(LaplaceClaimTypes.Provider) ?? "oidc"
                });
            return await _header.ResolveAsync(context, ct);
        }

        var record = await _apiKeys.ValidateAsync(presented, ct);
        return record is null
            ? new TenantContext("", "invalid_key", TenantContext.NoClaims)
            : new TenantContext(record.Tenant, "api_key",
                new Dictionary<string, string> { ["key_prefix"] = record.KeyPrefix });
    }

    public static string? PresentedKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (token.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal)) return token;
        }
        var headerKey = request.Headers["X-Api-Key"].ToString().Trim();
        return headerKey.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal) ? headerKey : null;
    }
}

internal sealed class ApiKeyEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LaplaceAuthOptions _options;

    public ApiKeyEnforcementMiddleware(RequestDelegate next, IOptions<LaplaceAuthOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    private static bool IsUnder(string path, string segment) =>
        path.Equals(segment, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
            && path.Length > segment.Length && path[segment.Length] == '/');

    private static bool PublicRead(HttpRequest request, string path) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/capabilities", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/billing/catalog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/billing/products", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/billing/plans", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/auth/config", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/auth/me", StringComparison.OrdinalIgnoreCase)
            || IsUnder(path, "/v1/auth/login"));

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        if (!IsUnder(path, "/v1") && !IsUnder(path, "/chess"))
        {
            await _next(context);
            return;
        }

        // Provider callbacks authenticate the signed payload, not a customer cookie.
        if (path.Equals("/v1/billing/webhooks/stripe", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(request.Method))
        {
            await _next(context);
            return;
        }

        if (IsUnder(path, "/v1/admin") || IsUnder(path, "/v1/billing/operator")
            || path.Equals("/v1/billing/catalog/sync", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatorAuth.IsAuthorized(request, _options))
            {
                await Reject(context, "operator_token_required", "This endpoint requires the operator credential.", 403);
                return;
            }
            await _next(context);
            return;
        }

        var tenant = await resolver.ResolveAsync(context, context.RequestAborted);
        if (tenant.AuthKind == "invalid_key")
        {
            await Reject(context, "invalid_api_key", "The provided API key is unknown, malformed, or revoked.");
            return;
        }

        if (_options.RequiresIdentity && tenant.AuthKind is not ("api_key" or "browser")
            && !PublicRead(request, path))
        {
            await Reject(context, "authentication_required", "Sign in or provide a Laplace API key for this workspace.");
            return;
        }

        if (tenant.AuthKind == "browser")
        {
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                && !HttpMethods.IsOptions(request.Method) && !SameOrigin(request))
            {
                await Reject(context, "same_origin_required", "Browser account changes must originate from this Laplace host.", 403);
                return;
            }

            // Read membership from PostgreSQL, not a role frozen into a cookie.
            // Removing a member affects the next request, including data reads.
            if (!IsUnder(path, "/v1/auth"))
            {
                var substrate = context.RequestServices.GetRequiredService<SubstrateClient>();
                await using var command = substrate.DataSource.CreateCommand("""
                    SELECT role FROM app.tenant_memberships
                    WHERE tenant_id = @tenant AND user_id = @user;
                    """);
                command.Parameters.AddWithValue("tenant", tenant.TenantId);
                command.Parameters.AddWithValue("user", Guid.Parse(tenant.Claims["user_id"]));
                var role = await command.ExecuteScalarAsync(context.RequestAborted) as string;
                if (role is null)
                {
                    await Reject(context, "workspace_membership_required", "This account no longer belongs to the selected workspace.", 403);
                    return;
                }
                context.Items["laplace.workspace_role"] = role;
                var managesBilling = IsUnder(path, "/v1/billing/keys")
                    || IsUnder(path, "/v1/billing/checkout")
                    || IsUnder(path, "/v1/billing/portal")
                    || (IsUnder(path, "/v1/billing/plans") && HttpMethods.IsPost(request.Method));
                if (managesBilling && role is not ("owner" or "admin"))
                {
                    await Reject(context, "workspace_admin_required", "A workspace owner or administrator must manage subscriptions and API keys.", 403);
                    return;
                }
            }
        }

        await _next(context);
    }

    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
            return Uri.TryCreate(origin, UriKind.Absolute, out var source)
                && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var target)
                && source.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)
                && source.Authority.Equals(target.Authority, StringComparison.OrdinalIgnoreCase);
        return request.Headers["Sec-Fetch-Site"].ToString() == "same-origin"
            || request.Headers["X-Laplace-Request"].ToString() == "1";
    }

    private static Task Reject(HttpContext context, string code, string message, int status = 401)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new Laplace.Api.Contracts.ErrorResponse(
            new Laplace.Api.Contracts.ErrorBody("authentication_error", code, message)));
    }
}

internal static class OperatorAuth
{
    public const string TokenHeader = "X-Laplace-Operator-Token";

    public static bool IsAuthorized(HttpRequest request, LaplaceAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OperatorToken)) return false;
        var presented = request.Headers[TokenHeader].ToString();
        if (string.IsNullOrWhiteSpace(presented)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(presented);
        var b = System.Text.Encoding.UTF8.GetBytes(options.OperatorToken);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
