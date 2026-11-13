using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The identifier law the HTTP, MCP and CLI surfaces share for tenants, participants, session keys and upload
/// participants, and the session id the MCP <c>recall</c> read takes. A conversation turn is written by the Engine
/// (<c>laplace turn</c>, through <see cref="Laplace.Ingestion.ConversationTurns"/>) and an upload by the Engine's
/// runtime witness (<see cref="Laplace.Ingestion.UserUploads"/>); nothing here builds or applies either.
/// </summary>
public static class ConversationContent
{
    /// <summary>
    /// Tenant ids, participants and session keys are names a request supplies, so this strict charset keeps them one
    /// segment of one line.
    /// </summary>
    private static readonly Regex IdentifierPattern =
        new(@"^[A-Za-z0-9._@-]{1,128}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidIdentifier(string? value) =>
        value is not null && IdentifierPattern.IsMatch(value);

    public static Hash128 SessionId(string tenant, string sessionKey)
    {
        if (!IsValidIdentifier(tenant))
            throw new ArgumentException($"tenant '{tenant}' is not a valid identifier", nameof(tenant));
        if (!IsValidIdentifier(sessionKey))
            throw new ArgumentException($"session key '{sessionKey}' is not a valid identifier", nameof(sessionKey));
        return SubstrateCanonicalIds.ConversationSession(tenant, sessionKey);
    }
}
