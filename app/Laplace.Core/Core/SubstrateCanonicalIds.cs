namespace Laplace.Engine.Core;

/// <summary>
/// Builders for substrate canonical keys (<c>substrate/…/v1</c>). A key's id resolves
/// through the same native hash as SQL realize.canonical_id(), source_id(),
/// relation_type_id(), and consensus_id(), and the builders produce byte-identical keys to
/// that SQL surface: <c>source_id('X')</c> equals
/// <c>realize.canonical_id('substrate/source/X/v1')</c>. A mistyped key is a different
/// entity, so segments are validated: an empty segment or one containing '/' throws.
/// </summary>
public static class SubstrateCanonicalKeys
{
    public const string Root = "substrate";

    /// <summary>Key for a decomposer/source identity — mirrors SQL <c>source_id(name)</c>.</summary>
    public static string Source(string name) => Versioned("source", name);

    /// <summary>
    /// Key for a conversation session. Tenant is part of the key, so a session key can
    /// never resolve into another tenant's session.
    /// </summary>
    public static string ConversationSession(string tenant, string sessionKey)
    {
        Validate(tenant, nameof(tenant));
        Validate(sessionKey, nameof(sessionKey));
        return $"{Root}/conversation/session/{tenant}/{sessionKey}/v1";
    }

    /// <summary>
    /// Free-form key under the substrate root: <c>substrate/a/b/c</c>, for families with
    /// no dedicated builder above.
    /// </summary>
    public static string Of(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
            throw new ArgumentException("a canonical key needs at least one segment", nameof(segments));
        foreach (var s in segments) Validate(s, nameof(segments));
        return $"{Root}/{string.Join('/', segments)}";
    }

    /// <summary>Free-form key with the trailing <c>/v1</c> version segment.</summary>
    public static string OfVersioned(params string[] segments) => Of(segments) + "/v1";

    private static string Versioned(string family, string name)
    {
        Validate(name, nameof(name));
        return $"{Root}/{family}/{name}/v1";
    }

    private static void Validate(string segment, string paramName)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("canonical key segment must not be empty or whitespace", paramName);
        if (segment.Contains('/'))
            throw new ArgumentException(
                $"canonical key segment '{segment}' contains '/': pass segments separately so the " +
                "key shape stays explicit", paramName);
    }
}

/// <summary>
/// Resolved ids for the keys above. Every id still goes through the native BLAKE3 in
/// <see cref="Hash128.OfCanonical"/> — this type composes the KEY, never the hash.
/// </summary>
public static class SubstrateCanonicalIds
{
    public static Hash128 Source(string name) => Hash128.OfCanonical(SubstrateCanonicalKeys.Source(name));

    public static Hash128 ConversationSession(string tenant, string sessionKey) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.ConversationSession(tenant, sessionKey));

    public static Hash128 Of(params string[] segments) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.Of(segments));

    public static Hash128 OfVersioned(params string[] segments) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.OfVersioned(segments));
}
