using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        await using (var conn = await ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            // Geometry is source-free: the content trajectory is the content itself (one row
            // per (entity_id, type)), so reconstruction descends the content DAG with no source
            // filter — the trajectory constituents ARE the reconstruction.
            cmd.CommandText = @"
                WITH RECURSIVE tree(id) AS (
                    SELECT @doc
                    UNION
                    SELECT unnest(public.laplace_trajectory_constituent_ids(p.trajectory))
                    FROM tree t
                    JOIN laplace.physicalities p
                      ON p.entity_id = t.id AND p.type = 1 AND p.trajectory IS NOT NULL
                )
                SELECT p.entity_id, p.n_constituents, (g.path)[1],
                       ST_X(g.geom), ST_Y(g.geom), ST_Z(g.geom), ST_M(g.geom)
                FROM laplace.physicalities p
                JOIN tree t ON t.id = p.entity_id
                CROSS JOIN LATERAL ST_DumpPoints(p.trajectory) AS g
                WHERE p.type = 1 AND p.trajectory IS NOT NULL";
            cmd.Parameters.AddWithValue("doc", documentId.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = ReadHash(r, 0);
                nConst[id] = r.GetInt32(1);
                if (!pts.TryGetValue(id, out var list)) pts[id] = list = new();
                list.Add((r.GetInt32(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)));
            }
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

    private static Hash128 ReadHash(NpgsqlDataReader r, int ord)
    {
        var bytes = (byte[])r[ord];
        return new Hash128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
    }
}
