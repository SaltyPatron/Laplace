using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetRelations
{
    // Bucketed by relation-name LENGTH. The flat array this replaced was scanned linearly,
    // so every row of assertions.csv cost up to RelMap.Count SequenceEqual calls — and the
    // rows that resolve to nothing paid the full scan before being dropped. Length is a free
    // discriminator (the span is already in hand) and ConceptNet's names spread thinly across
    // lengths, so a bucket holds one or two entries: ~1 comparison instead of ~39.
    //
    // A byte-keyed dictionary was the other option and is rejected on allocation: hashing a
    // ReadOnlySpan<byte> against a Dictionary<byte[],_> needs either a per-row GetString or a
    // custom alternate-lookup comparer, and this path runs tens of millions of times.
    private static readonly (byte[] RelUtf8, string TypeName, bool Negated)[]?[] ByLength = BuildByLength();

    /*
     * ConceptNet's /r/dbpedia/* lane, mapped onto GENERIC manifest relations rather than
     * minted as DBPEDIA_<REL>. consensus.id is blake3(subject‖type‖object): a source-scoped
     * type would give the same fact a private cell, so dbpedia's "Paris is France's capital"
     * would never merge with the AT_LOCATION edge ConceptNet's own AtLocation lane emits.
     * Mapped, both land on one cell and witness_count actually climbs -- which is the whole
     * point of a fold. Provenance stays where it belongs, on AttestationRow.SourceId.
     *
     * TARGETS ARE RESTRICTED to types ConceptNetSource.BuildRelations already declares
     * (AT_LOCATION, IS_A, RELATED_TO via RelMap; HAS_LANGUAGE from the fixed set). A
     * decomposer emitting an undeclared relation faults the native attestation path, so
     * widening this table means widening the declaration in the same commit.
     *
     * Flip is per-entry because dbpedia's subject order is not the manifest's. dbpedia says
     * (France, capital, Paris); AT_LOCATION reads "subject is located at object", so the
     * edge is emitted as (Paris, AT_LOCATION, France). Relations left out of this table
     * still return false -- an uncertain mapping is worse than a dropped edge, because a
     * wrong one silently corrupts a cell that other sources are also voting in.
     */
    private static readonly (byte[] RelUtf8, string TypeName, bool Flip)[] DbpediaMap =
    [
        ("dbpedia/capital"u8.ToArray(),      "AT_LOCATION",  true),
        ("dbpedia/language"u8.ToArray(),     "HAS_LANGUAGE", false),
        ("dbpedia/genus"u8.ToArray(),        "IS_A",         false),
        ("dbpedia/genre"u8.ToArray(),        "IS_A",         false),
        ("dbpedia/occupation"u8.ToArray(),   "IS_A",         false),
        ("dbpedia/knownFor"u8.ToArray(),     "RELATED_TO",   false),
        ("dbpedia/influencedBy"u8.ToArray(), "RELATED_TO",   false),
        ("dbpedia/field"u8.ToArray(),        "RELATED_TO",   false),
    ];

    public static bool TryResolveType(ReadOnlySpan<byte> relationUri, out string typeName)
        => TryResolveType(relationUri, out typeName, out _, out _);

    public static bool TryResolveType(
        ReadOnlySpan<byte> relationUri, out string typeName, out bool flip)
        => TryResolveType(relationUri, out typeName, out flip, out _);

    /// <summary>
    /// <paramref name="negated"/> is set for the Not* relations, which map onto the
    /// relation they DENY. The caller negates the row's magnitude so it folds as a Refute
    /// against that relation's cell instead of into a separate positive NOT_* type where
    /// it could never contest anything. Carried in the lookup table so the hot path costs
    /// no allocation and no second comparison.
    /// </summary>
    public static bool TryResolveType(
        ReadOnlySpan<byte> relationUri, out string typeName, out bool flip, out bool negated)
    {
        typeName = "";
        flip = false;
        negated = false;
        if (relationUri.Length < 4
            || relationUri[0] != (byte)'/'
            || relationUri[1] != (byte)'r'
            || relationUri[2] != (byte)'/')
            return false;

        ReadOnlySpan<byte> rel = relationUri[3..];
        if (rel.StartsWith("dbpedia/"u8))
        {
            foreach (var (key, name, f) in DbpediaMap)
            {
                if (rel.SequenceEqual(key))
                {
                    typeName = name;
                    flip = f;
                    return true;
                }
            }
            return false;
        }

        if (rel.Length >= ByLength.Length)
            return false;
        var bucket = ByLength[rel.Length];
        if (bucket is null)
            return false;

        foreach (var (key, name, neg) in bucket)
        {
            if (rel.SequenceEqual(key))
            {
                typeName = name;
                negated = neg;
                return true;
            }
        }
        return false;
    }

    private static (byte[] RelUtf8, string TypeName, bool Negated)[]?[] BuildByLength()
    {
        var byLen = new Dictionary<int, List<(byte[], string, bool)>>();
        int max = 0;

        foreach (var (rel, typeName) in ConceptNetSource.RelMap)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(rel);
            if (utf8.Length > max) max = utf8.Length;
            if (!byLen.TryGetValue(utf8.Length, out var bucket))
                byLen[utf8.Length] = bucket = new List<(byte[], string, bool)>(2);
            bucket.Add((utf8, typeName, ConceptNetSource.NegatedRelations.Contains(rel)));
        }

        var table = new (byte[] RelUtf8, string TypeName, bool Negated)[]?[max + 1];
        foreach (var (len, bucket) in byLen)
            table[len] = bucket.ToArray();
        return table;
    }
}
