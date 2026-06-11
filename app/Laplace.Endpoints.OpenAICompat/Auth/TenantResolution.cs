namespace Laplace.Endpoints.OpenAICompat.Auth;

/// <summary>
/// The identity a request acts as. Everything downstream (billing stores, entitlements,
/// rate-limit partitions, usage ledger) keys on <see cref="TenantId"/> — that string is the
/// seam future auth modes plug into without touching any consumer.
/// </summary>
public sealed record TenantContext(
    string TenantId,
    string AuthKind,
    IReadOnlyDictionary<string, string> Claims)
{
    public static readonly IReadOnlyDictionary<string, string> NoClaims =
        new Dictionary<string, string>();
}

/// <summary>
/// Resolves the tenant for a request. Selected by LAPLACE_AUTH_MODE:
/// "header" (default) trusts X-Laplace-Tenant; a future "b2c" mode validates a JWT bearer
/// and derives the tenant from its oid/sub claim — consumers never change.
/// </summary>
public interface ITenantResolver
{
    ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct);
}

/// <summary>Pre-auth behavior: X-Laplace-Tenant header, defaulting to "local-dev".</summary>
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
