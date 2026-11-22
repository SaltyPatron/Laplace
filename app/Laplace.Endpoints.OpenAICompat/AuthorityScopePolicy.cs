using Laplace.Engine.Core;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;

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
        double? minTrust = null)
    {
        var scopeSpec = !string.IsNullOrWhiteSpace(explicitScope)
            ? explicitScope.Trim()
            : request.Headers[ScopeHeader].ToString().Trim();
        var rawWitnessHeader = request.Headers[WitnessesHeader].ToString();

        var witnessIds = new List<Hash128>();
        bool scoped = !string.IsNullOrWhiteSpace(scopeSpec)
            && !string.Equals(scopeSpec, "world", StringComparison.OrdinalIgnoreCase);

        if (explicitWitnesses is not null)
        {
            scoped = true;
            foreach (var w in explicitWitnesses)
            {
                if (!TryParseWitnessId(w, out var id))
                    return Refused("invalid_authority_witness",
                        $"Witness id '{w}' must be a 32-hex entity id.",
                        StatusCodes.Status400BadRequest);
                witnessIds.Add(id);
            }
        }
        else if (!string.IsNullOrWhiteSpace(rawWitnessHeader))
        {
            scoped = true;
            foreach (var part in rawWitnessHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseWitnessId(part, out var id))
                    return Refused("invalid_authority_witness",
                        $"Header {WitnessesHeader} contains invalid witness id '{part}'.",
                        StatusCodes.Status400BadRequest);
                witnessIds.Add(id);
            }
        }
        else if (TryParseWitnessId(scopeSpec, out var singleId))
        {
            scoped = true;
            witnessIds.Add(singleId);
        }
        else if (string.Equals(scopeSpec, "tenant", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(scopeSpec, "private", StringComparison.OrdinalIgnoreCase))
        {
            scoped = true;
            // Deterministic tenant-scoped witness root; if a caller specifies "none"/"empty", stay empty and fail closed.
            if (!string.IsNullOrWhiteSpace(tenant.TenantId))
                witnessIds.Add(Hash128.Text(tenant.TenantId));
        }
        else if (string.Equals(scopeSpec, "empty", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(scopeSpec, "none", StringComparison.OrdinalIgnoreCase))
        {
            scoped = true;
        }
        else if (scoped && string.Equals(scopeSpec, "deny", StringComparison.OrdinalIgnoreCase))
        {
            return Refused("authority_refused",
                "Authority disposition refuses execution for this request scope.",
                StatusCodes.Status403Forbidden);
        }
        else if (scoped)
        {
            return Refused("unknown_authority_scope",
                $"Authority scope '{scopeSpec}' cannot resolve a governed witness package.",
                StatusCodes.Status403Forbidden);
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
        double effectiveMinTrust = minTrust is { } mt && double.IsFinite(mt)
            ? Math.Clamp(mt, 0.0, 1.0)
            : 0.0;

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
