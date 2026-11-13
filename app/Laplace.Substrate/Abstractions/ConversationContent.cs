using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// What is left of the conversational provenance this application once recorded in <c>laplace.*</c>: the
/// identifier law the HTTP, MCP and CLI surfaces share for tenants, users and session keys, the per-tenant
/// prompt and response source identities the user-content export still reads, and the session id the MCP
/// <c>recall</c> read takes. A conversation turn is written by the Engine (<c>laplace turn</c>, through
/// <see cref="Laplace.Ingestion.ConversationTurns"/>); nothing here builds or applies one.
/// </summary>
public static class ConversationContent
{
    public const string PromptSourceBase = "UserPrompt";
    public const string ResponseSourceBase = "Response";

    public static readonly Hash128 SessionType = EntityTypeRegistry.ConversationSession;

    /// <summary>
    /// Tenant ids and session keys become canonical-key segments and header tenants are
    /// caller-controlled, so this strict charset protects key integrity.
    /// </summary>
    private static readonly Regex IdentifierPattern =
        new(@"^[A-Za-z0-9._@-]{1,128}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidIdentifier(string? value) =>
        value is not null && IdentifierPattern.IsMatch(value);

    /// <summary>Per-tenant witness identities; authorization scope is not source trust.</summary>
    public readonly record struct TenantScope(
        string Tenant,
        Hash128 PromptSource,
        Hash128 ResponseSource)
    {
        public string PromptSourceName => $"{PromptSourceBase}@{Tenant}";
        public string ResponseSourceName => $"{ResponseSourceBase}@{Tenant}";
    }

    public static TenantScope Resolve(string tenant)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException(
                $"tenant '{tenant}' is not a valid identifier ([A-Za-z0-9._@-]{{1,128}})", nameof(tenant));
        return new TenantScope(
            tenant,
            SubstrateCanonicalIds.Source($"{PromptSourceBase}@{tenant}"),
            SubstrateCanonicalIds.Source($"{ResponseSourceBase}@{tenant}"));
    }

    public static Hash128 SessionId(string tenant, string sessionKey)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException($"tenant '{tenant}' is not a valid identifier", nameof(tenant));
        if (!IsValidIdentifier(sessionKey))
            throw new ArgumentException($"session key '{sessionKey}' is not a valid identifier", nameof(sessionKey));
        return SubstrateCanonicalIds.ConversationSession(tenant, sessionKey);
    }

    /// <summary>The relation a prompt's membership in its session was attested by.</summary>
    public const string MembershipRelation = "APPEARS_IN";
}
