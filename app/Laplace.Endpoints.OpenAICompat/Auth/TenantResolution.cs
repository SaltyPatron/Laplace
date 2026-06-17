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
