namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical relation names DERIVED from a C# symbol, so an emit site never carries a
/// name literal.
///
/// The vocabulary law (isa-gate g3) is that a governed relation name may be spelled in
/// exactly one place per source: the source's <c>Relations</c> roster, which is the
/// declaration span the gate exempts. Its baseline froze on 2026-08-03 and is shrink-only,
/// so a relation added after that date cannot introduce a new literal at a query site --
/// it has to reach the registry some other way.
///
/// The AgentTrace lane solved this first, privately, by deriving the surface from an enum
/// member name (HasRole -> HAS_ROLE). That conversion is not specific to agent traces and
/// every future relation needs it, so it lives here and that lane calls it (§15: one body
/// for one truth).
/// </summary>
public static class RelationSymbol
{
    /// <summary>
    /// PascalCase symbol to canonical relation surface: HasNormalizationForm ->
    /// HAS_NORMALIZATION_FORM. Digits attach to the run they follow (Iso639_1 ->
    /// ISO639_1), and an existing underscore is preserved rather than doubled.
    /// </summary>
    public static string Canonical(string symbol)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        var sb = new System.Text.StringBuilder(symbol.Length + 4);
        foreach (char c in symbol)
        {
            if (char.IsUpper(c) && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Same, for a field named after the relation it resolves: the caller passes
    /// <c>nameof(RelTypeHasNormalizationForm)</c> and the <c>RelType</c> prefix is dropped.
    /// A rename that breaks the correspondence fails loudly at the registry lookup rather
    /// than silently resolving a different relation.
    /// </summary>
    public static string CanonicalFromField(string fieldName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        const string Prefix = "RelType";
        return Canonical(fieldName.StartsWith(Prefix, StringComparison.Ordinal)
            ? fieldName[Prefix.Length..]
            : fieldName);
    }
}
