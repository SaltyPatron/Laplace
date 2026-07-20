namespace Laplace.Engine.Core;

/// <summary>
/// The one place C# composes a substrate canonical key.
///
/// Law (CLAUDE.md): ids are NEVER constructed outside the system — canonical_id(),
/// source_id(), relation_type_id() and consensus_id() resolve through the native hash.
/// 152 hand-typed canonical-key literals (raw <c>OfCanonical</c> calls) were scattered
/// across the app, where a single typo mints a DIFFERENT entity and nothing complains:
/// the write succeeds, the id is simply wrong forever.
///
/// These builders produce byte-identical keys to the SQL surface. Proven, not assumed:
/// <c>source_id('WordNetDecomposer')</c> and
/// <c>canonical_id('substrate/source/WordNetDecomposer/v1')</c> both resolve to
/// 4b1ee33be3034910df7629b2948cde35 on the live DB, and SubstrateCanonicalIdsTests
/// pins that shape.
///
/// Segments are validated rather than trusted: an empty segment or one containing a
/// path separator would silently change the key, so both throw.
/// </summary>
public static class SubstrateCanonicalKeys
{
    public const string Root = "substrate";

    /// <summary>Key for a decomposer/source identity — mirrors SQL <c>source_id(name)</c>.</summary>
    public static string Source(string name) => Versioned("source", name);

    /// <summary>Key for a witness trust class (AcademicCurated, StructuredCorpus, ...).</summary>
    public static string TrustClass(string name) => Versioned("trust_class", name);

    /// <summary>Key for a probationary POS tag minted under a named tagset.</summary>
    public static string PosProbationary(string tagset, string tag)
    {
        Validate(tagset, nameof(tagset));
        Validate(tag, nameof(tag));
        return $"{Root}/pos/probationary/{tagset}/{tag}/v1";
    }

    /// <summary>
    /// Free-form key under the substrate root: <c>substrate/a/b/c</c>. Use when the
    /// family has no dedicated builder above; prefer adding a builder over spreading
    /// raw segment lists back through the codebase.
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

    public static Hash128 TrustClass(string name) => Hash128.OfCanonical(SubstrateCanonicalKeys.TrustClass(name));

    public static Hash128 PosProbationary(string tagset, string tag) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.PosProbationary(tagset, tag));

    public static Hash128 Of(params string[] segments) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.Of(segments));

    public static Hash128 OfVersioned(params string[] segments) =>
        Hash128.OfCanonical(SubstrateCanonicalKeys.OfVersioned(segments));
}
