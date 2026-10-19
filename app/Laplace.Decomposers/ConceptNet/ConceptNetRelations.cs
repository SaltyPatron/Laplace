using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetRelations
{
    // Bucketed by relation-name length: the span length is free, and ConceptNet's names
    // spread thinly across lengths, so a lookup is about one SequenceEqual per row with no
    // allocation (a byte[]-keyed dictionary would need a per-row string or custom comparer).
    private static readonly (byte[] RelUtf8, string TypeName, bool Negated)[]?[] ByLength = BuildByLength();

    // /r/dbpedia/* keeps dbpedia's token and dbpedia's direction. capital is
    // (country, dbpedia/capital, city) because that is the row.
    private static readonly (byte[] RelUtf8, string TypeName, bool Flip)[] DbpediaMap =
    [
        ("dbpedia/capital"u8.ToArray(),      "dbpedia/capital",      false),
        ("dbpedia/language"u8.ToArray(),     "dbpedia/language",     false),
        ("dbpedia/genus"u8.ToArray(),        "dbpedia/genus",        false),
        ("dbpedia/genre"u8.ToArray(),        "dbpedia/genre",        false),
        ("dbpedia/occupation"u8.ToArray(),   "dbpedia/occupation",   false),
        ("dbpedia/knownFor"u8.ToArray(),     "dbpedia/knownFor",     false),
        ("dbpedia/influencedBy"u8.ToArray(), "dbpedia/influencedBy", false),
        ("dbpedia/field"u8.ToArray(),        "dbpedia/field",        false),
    ];

    public static bool TryResolveType(ReadOnlySpan<byte> relationUri, out string typeName)
        => TryResolveType(relationUri, out typeName, out _, out _);

    public static bool TryResolveType(
        ReadOnlySpan<byte> relationUri, out string typeName, out bool flip)
        => TryResolveType(relationUri, out typeName, out flip, out _);

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
