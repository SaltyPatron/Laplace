namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical relation names derived from a C# symbol, so an emit site never carries a
/// name literal. A governed relation name is spelled in one place per source, its
/// <c>Relations</c> roster; every other site derives the surface from a symbol name
/// (HasRole -> HAS_ROLE) and resolves it through the registry.
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
