using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetRelations
{
    private static readonly (byte[] RelUtf8, string TypeName)[] Known = BuildKnown();

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

        foreach (var (key, name) in Known)
        {
            if (rel.SequenceEqual(key))
            {
                typeName = name;
                return true;
            }
        }
        return false;
    }

    private static (byte[] RelUtf8, string TypeName)[] BuildKnown()
    {
        var list = new List<(byte[], string)>(ConceptNetSource.RelMap.Count);
        foreach (var (rel, typeName) in ConceptNetSource.RelMap)
            list.Add((System.Text.Encoding.UTF8.GetBytes(rel), typeName));
        return list.ToArray();
    }
}
