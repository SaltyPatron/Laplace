using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Exact text-content reconstruction from one content/composition id. This is the shared
/// read-side implementation; callers must not hand-roll trajectory recursion in CLI/API/UI.
/// It returns the codepoint sequence that composes the id and makes no claim about a target
/// presentation modality beyond UTF-8 text.
/// </summary>
public static class NpgsqlContentReconstructor
{
    public static async Task<byte[]> ReconstructUtf8Async(
        NpgsqlDataSource dataSource,
        Hash128 rootId,
        CancellationToken ct = default)
    {
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.LoadDefault();

        var idToCp = new Dictionary<Hash128, uint>(CodepointPerfcache.Records.Length);
        ReadOnlySpan<CodepointRecord> records = CodepointPerfcache.Records;
        for (int i = 0; i < records.Length; i++)
            idToCp[records[i].Hash] = records[i].Codepoint;

        var nConstituents = new Dictionary<Hash128, int>();
        var points = new Dictionary<Hash128, List<(int Path, double X, double Y, double Z, double M)>>();

        var rows = await NpgsqlSubstrateReads.TrajectoryTreeDumpPointsAsync(
            dataSource, rootId.ToBytes(), ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            Hash128 id = ReadHash(row.Id);
            nConstituents[id] = row.NConstituents;
            if (!points.TryGetValue(id, out var list))
                points[id] = list = [];
            list.Add((row.PathIndex, row.X, row.Y, row.Z, row.M));
        }

        var children = new Dictionary<Hash128, Hash128[]>(points.Count);
        foreach (var (id, list) in points)
        {
            list.Sort(static (a, b) => a.Path.CompareTo(b.Path));
            var xyzm = new double[list.Count * 4];
            for (int i = 0; i < list.Count; i++)
            {
                xyzm[i * 4] = list[i].X;
                xyzm[i * 4 + 1] = list[i].Y;
                xyzm[i * 4 + 2] = list[i].Z;
                xyzm[i * 4 + 3] = list[i].M;
            }
            Hash128[] vertices = Trajectory.Constituents(xyzm);
            int take = Math.Min(nConstituents[id], vertices.Length);
            children[id] = vertices[..take];
        }

        var sb = new StringBuilder();
        Emit(rootId, children, idToCp, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Emit(
        Hash128 id,
        IReadOnlyDictionary<Hash128, Hash128[]> children,
        IReadOnlyDictionary<Hash128, uint> idToCp,
        StringBuilder sb)
    {
        if (idToCp.TryGetValue(id, out uint cp))
        {
            sb.Append(char.ConvertFromUtf32((int)cp));
            return;
        }
        if (children.TryGetValue(id, out var kids))
            foreach (Hash128 child in kids)
                Emit(child, children, idToCp, sb);
    }

    private static Hash128 ReadHash(byte[] bytes)
    {
        if (bytes.Length != 16)
            throw new InvalidDataException($"expected 16-byte entity id, got {bytes.Length}");
        return new Hash128(
            BitConverter.ToUInt64(bytes, 0),
            BitConverter.ToUInt64(bytes, 8));
    }
}
