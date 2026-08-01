using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Cli;

internal static class ContentRoundtrip
{
    public static Hash128 PromptSource => UserPromptContent.Source;

    public static Task BootstrapAsync(ISubstrateWriter writer, CancellationToken ct = default)
        => writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    public static async Task<Hash128> RecordAsync(
        ISubstrateWriter writer, byte[] utf8, CancellationToken ct = default)
    {
        if (!UserPromptContent.TryBuildWitnessChange(utf8, "prompt", out var change, out var rootId))
            return Hash128.Zero;
        await writer.ApplyAsync(change, ct);
        return rootId;
    }

    public static async Task<byte[]> ReconstructAsync(
        NpgsqlDataSource ds, Hash128 documentId, CancellationToken ct = default)
    {
        var idToCp = new Dictionary<Hash128, uint>(1_114_112);
        ReadOnlySpan<CodepointRecord> recs = CodepointPerfcache.Records;
        for (int i = 0; i < recs.Length; i++) idToCp[recs[i].Hash] = recs[i].Codepoint;

        var nConst = new Dictionary<Hash128, int>();
        var pts = new Dictionary<Hash128, List<(int path, double x, double y, double z, double m)>>();

        var rows = await NpgsqlSubstrateReads.TrajectoryTreeDumpPointsAsync(
            ds, documentId.ToBytes(), ct);
        foreach (var row in rows)
        {
            var id = ReadHash(row.Id);
            nConst[id] = row.NConstituents;
            if (!pts.TryGetValue(id, out var list)) pts[id] = list = new();
            list.Add((row.PathIndex, row.X, row.Y, row.Z, row.M));
        }

        var children = new Dictionary<Hash128, Hash128[]>(pts.Count);
        foreach (var (id, list) in pts)
        {
            list.Sort((a, b) => a.path.CompareTo(b.path));
            var xyzm = new double[list.Count * 4];
            for (int i = 0; i < list.Count; i++)
            {
                xyzm[i * 4] = list[i].x; xyzm[i * 4 + 1] = list[i].y;
                xyzm[i * 4 + 2] = list[i].z; xyzm[i * 4 + 3] = list[i].m;
            }
            Hash128[] verts = Trajectory.Constituents(xyzm);
            int take = Math.Min(nConst[id], verts.Length);
            children[id] = verts[..take];
        }

        var sb = new StringBuilder();
        Emit(documentId, children, idToCp, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Emit(Hash128 id, Dictionary<Hash128, Hash128[]> children,
                             Dictionary<Hash128, uint> idToCp, StringBuilder sb)
    {
        if (idToCp.TryGetValue(id, out uint cp)) { sb.Append(char.ConvertFromUtf32((int)cp)); return; }
        if (children.TryGetValue(id, out var kids))
            foreach (var k in kids) Emit(k, children, idToCp, sb);
    }

    private static Hash128 ReadHash(byte[] bytes)
        => new(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
}
