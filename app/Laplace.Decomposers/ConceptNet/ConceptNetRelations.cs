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
    private static readonly (byte[] RelUtf8, string TypeName)[]?[] ByLength = BuildByLength();

    public static bool TryResolveType(ReadOnlySpan<byte> relationUri, out string typeName)
    {
        typeName = "";
        if (relationUri.Length < 4
            || relationUri[0] != (byte)'/'
            || relationUri[1] != (byte)'r'
            || relationUri[2] != (byte)'/')
            return false;

        ReadOnlySpan<byte> rel = relationUri[3..];
        if (rel.StartsWith("dbpedia/"u8))
            return false;

        if (rel.Length >= ByLength.Length)
            return false;
        var bucket = ByLength[rel.Length];
        if (bucket is null)
            return false;

        foreach (var (key, name) in bucket)
        {
            if (rel.SequenceEqual(key))
            {
                typeName = name;
                return true;
            }
        }
        return false;
    }

    private static (byte[] RelUtf8, string TypeName)[]?[] BuildByLength()
    {
        var byLen = new Dictionary<int, List<(byte[], string)>>();
        int max = 0;

        foreach (var (rel, typeName) in ConceptNetSource.RelMap)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(rel);
            if (utf8.Length > max) max = utf8.Length;
            if (!byLen.TryGetValue(utf8.Length, out var bucket))
                byLen[utf8.Length] = bucket = new List<(byte[], string)>(2);
            bucket.Add((utf8, typeName));
        }

        var table = new (byte[] RelUtf8, string TypeName)[]?[max + 1];
        foreach (var (len, bucket) in byLen)
            table[len] = bucket.ToArray();
        return table;
    }
}
