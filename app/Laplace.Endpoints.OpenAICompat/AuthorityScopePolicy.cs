using Laplace.Engine.Core;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Resolves fail-closed authority packages (#1708) into C <c>LpEngineAuthorityScope</c>.
/// An explicit empty witness allowlist refuses before coupling rather than opening the world.
/// </summary>
internal static class AuthorityScopePolicy
{
    public const string ScopeHeader = "X-Laplace-Authority-Scope";
    public const string WitnessesHeader = "X-Laplace-Witnesses";

    internal readonly record struct Resolution(
        EngineAuthorityScope Scope,
        string? ErrorCode,
        string? ErrorMessage,
        int StatusCode)
    {
        public bool Allowed => ErrorCode is null;
    }

    public static Resolution Resolve(
        HttpRequest request,
        TenantContext tenant,
        uint requiredCapabilities,
        bool requestWrite,
        string? explicitScope = null,
        IReadOnlyList<string>? explicitWitnesses = null,
        double? minTrust = null,
        IKnowledgePackageStore? packages = null)
    {
        packages ??= BillingServiceFallback.GetPackages(request.HttpContext?.RequestServices);

        var scopeSpec = !string.IsNullOrWhiteSpace(explicitScope)
            ? explicitScope.Trim()
            : request.Headers[ScopeHeader].ToString().Trim();
        var rawWitnessHeader = request.Headers[WitnessesHeader].ToString();

        var witnessIds = new List<Hash128>();
        bool scoped = !string.IsNullOrWhiteSpace(scopeSpec)
            && !string.Equals(scopeSpec, "world", StringComparison.OrdinalIgnoreCase);
        bool packageScoped = false;
        double packageMinTrust = 0.0;

        if (scoped)
        {
            if (string.Equals(scopeSpec, "deny", StringComparison.OrdinalIgnoreCase))
            {
                return Refused("authority_refused",
                    "Authority disposition refuses execution for this request scope.",
                    StatusCodes.Status403Forbidden);
            }
            if (string.Equals(scopeSpec, "empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scopeSpec, "none", StringComparison.OrdinalIgnoreCase))
            {
                // Keep witnessIds empty so the fail-closed check below refuses with authority_empty_scope.
            }
            else if (TryParseWitnessId(scopeSpec, out var singleId))
            {
                witnessIds.Add(singleId);
            }
            else if (string.Equals(scopeSpec, "tenant", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(scopeSpec, "private", StringComparison.OrdinalIgnoreCase))
            {
                // Deterministic tenant-scoped witness root; if empty, fail closed below.
                if (!string.IsNullOrWhiteSpace(tenant.TenantId))
                    witnessIds.Add(Hash128.Text(tenant.TenantId));
            }
            else
            {
                packageScoped = true;
                var packageIds = scopeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (packageIds.Length == 0)
                    return Refused("authority_empty_scope",
                        "Scoped authority package list resolved zero permitted witnesses; failing closed.",
                        StatusCodes.Status403Forbidden);
                if (packages is null)
                    return Refused("authority_package_denied",
                        $"Authority package scope '{scopeSpec}' cannot be resolved without a knowledge package store.",
                        StatusCodes.Status403Forbidden);

                var resolvedPkg = packages.ResolveForTenant(tenant.TenantId, packageIds);
                if (!resolvedPkg.Authorized)
                {
                    return Refused(
                        resolvedPkg.ErrorCode ?? "authority_package_denied",
                        resolvedPkg.ErrorMessage ?? $"Authority scope '{scopeSpec}' is denied for workspace '{tenant.TenantId}'.",
                        StatusCodes.Status403Forbidden);
                }

                witnessIds.AddRange(resolvedPkg.WitnessIds);
                packageMinTrust = resolvedPkg.MinTrust;
            }
        }

        if (explicitWitnesses is not null)
        {
            if (!TryParseWitnessList(explicitWitnesses, out var requested, out var badCode, out var badMessage))
                return Refused(badCode!, badMessage!, StatusCodes.Status400BadRequest);
            scoped = true;
            witnessIds = packageScoped
                ? IntersectWitnesses(witnessIds, requested)
                : requested;
        }
        else if (!string.IsNullOrWhiteSpace(rawWitnessHeader))
        {
            var headerParts = rawWitnessHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!TryParseWitnessList(headerParts, out var requested, out var badCode, out var badMessage, WitnessesHeader))
                return Refused(badCode!, badMessage!, StatusCodes.Status400BadRequest);
            scoped = true;
            witnessIds = packageScoped
                ? IntersectWitnesses(witnessIds, requested)
                : requested;
        }

        // Fail closed (#1708): scoped request with empty witness allowlist must refuse, never widen.
        if (scoped && witnessIds.Count == 0)
            return Refused("authority_empty_scope",
                "Scoped authority allowlist resolved zero permitted witnesses; failing closed.",
                StatusCodes.Status403Forbidden);

        uint capabilities = ResolveTenantCapabilities(tenant);
        if ((capabilities & requiredCapabilities) != requiredCapabilities)
            return Refused("insufficient_authority",
                $"Caller lacks required capability mask 0x{requiredCapabilities:x} (held 0x{capabilities:x}).",
                StatusCodes.Status403Forbidden);

        bool allowWrite = requestWrite && (capabilities & (uint)LaplaceNative.LP_CAP_ATTEST) != 0;
        if (requestWrite && !allowWrite)
            return Refused("authority_write_forbidden",
                "Authority scope forbids state attestation or writes.",
                StatusCodes.Status403Forbidden);

        var distinct = witnessIds.Distinct().ToArray();
        double callerMinTrust = minTrust is { } mt && double.IsFinite(mt)
            ? Math.Clamp(mt, 0.0, 1.0)
            : 0.0;
        double effectiveMinTrust = Math.Max(callerMinTrust, packageMinTrust);

        return new Resolution(
            new EngineAuthorityScope(
                Witnesses: distinct,
                Scoped: scoped,
                Capabilities: capabilities,
                Disposition: (int)LaplaceNative.LP_DISPOSE_ALLOW,
                MinTrust: effectiveMinTrust,
                AllowWrite: allowWrite),
            null,
            null,
            StatusCodes.Status200OK);
    }

    private static bool TryParseWitnessList(
        IEnumerable<string> values,
        out List<Hash128> ids,
        out string? errorCode,
        out string? errorMessage,
        string? headerName = null)
    {
        ids = new List<Hash128>();
        errorCode = null;
        errorMessage = null;
        foreach (var raw in values)
        {
            if (!TryParseWitnessId(raw, out var id))
            {
                errorCode = "invalid_authority_witness";
                errorMessage = headerName is null
                    ? $"Witness id '{raw}' must be a 32-hex entity id."
                    : $"Header {headerName} contains invalid witness id '{raw}'.";
                return false;
            }
            ids.Add(id);
        }
        return true;
    }

    private static List<Hash128> IntersectWitnesses(List<Hash128> allowed, List<Hash128> requested)
    {
        if (allowed.Count == 0 || requested.Count == 0)
            return new List<Hash128>();
        var allowedSet = allowed.ToHashSet();
        return requested.Where(allowedSet.Contains).Distinct().ToList();
    }

    private static uint ResolveTenantCapabilities(TenantContext tenant)
    {
        if (tenant.AuthKind == "invalid_key")
            return (uint)LaplaceNative.LP_CAP_NONE;

        if (tenant.Claims.TryGetValue("capabilities", out var explicitCaps)
            && uint.TryParse(explicitCaps, out var parsed))
            return parsed;

        uint baseCaps = (uint)(LaplaceNative.LP_CAP_READ
                             | LaplaceNative.LP_CAP_ATTEST
                             | LaplaceNative.LP_CAP_EXPORT);
        if (tenant.AuthKind is "operator" or "dev")
            baseCaps |= (uint)(LaplaceNative.LP_CAP_ASSERT | LaplaceNative.LP_CAP_CONTAIN | LaplaceNative.LP_CAP_INGEST);
        return baseCaps;
    }

    private static bool TryParseWitnessId(string? value, out Hash128 id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length != 32 || !s.All(Uri.IsHexDigit)) return false;
        id = Hash128.FromBytes(Convert.FromHexString(s));
        return true;
    }

    private static Resolution Refused(string code, string message, int status) =>
        new(new EngineAuthorityScope([], Scoped: true, 0, (int)LaplaceNative.LP_DISPOSE_REFUSE, 0.0, false),
            code, message, status);
}
