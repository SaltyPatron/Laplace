using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Cli;

/// <summary>
/// The non-attestation content path: record a text's composed tiers into the
/// substrate (entities + CONTENT physicalities whose trajectory packs the
/// ordered constituent ids), then reconstruct the exact bytes by reading those
/// trajectories back out of the database. Pure content — no attestations. The
/// split point for the attestation path is after <see cref="RecordAsync"/>
/// returns the document id: a source would attest onto these content-addressed
/// ids post-dedup. Generic over the tier→type map; text fills it here.
/// </summary>
internal static class ContentRoundtrip
{
    // Prompt = non-attested user data. Its own source entity + trust class.
    public static readonly Hash128 PromptSource = Hash128.OfCanonical("substrate/source/UserPrompt/v1");
    public static readonly Hash128 PromptTrust  = Hash128.OfCanonical("substrate/trust_class/UserPromptContent/v1");

    // Text modality tier → type entity (T0 Codepoint already seeded).
    private static readonly Hash128 TGrapheme = Hash128.OfCanonical("substrate/type/Grapheme/v1");
    private static readonly Hash128 TWord     = Hash128.OfCanonical("substrate/type/Word/v1");
    private static readonly Hash128 TSentence = Hash128.OfCanonical("substrate/type/Sentence/v1");
    private static readonly Hash128 TDocument = Hash128.OfCanonical("substrate/type/Document/v1");

    private static Hash128 TypeForTier(byte tier) => tier switch
    {
        1 => TGrapheme, 2 => TWord, 3 => TSentence, 4 => TDocument,
        _ => TDocument,
    };

    /// <summary>Register the prompt source + the text tier types as plain
    /// entities. NO attestations — content is non-attested; the source's
    /// trust class + any meaning are the deferred attestation path. (Trust
    /// matters only when a source attests; pure content weights nothing.)</summary>
    public static async Task BootstrapAsync(ISubstrateWriter writer, CancellationToken ct = default)
    {
        var b = new SubstrateChangeBuilder(PromptSource, "bootstrap/UserPrompt", parentIntentId: null);
        b.AddEntity(PromptSource, /*tier*/ 0, BootstrapIntentBuilder.SourceTypeId, firstObservedBy: PromptSource);
        b.AddEntity(TGrapheme, 0, BootstrapIntentBuilder.TypeMetaTypeId, PromptSource);
        b.AddEntity(TWord,     0, BootstrapIntentBuilder.TypeMetaTypeId, PromptSource);
        b.AddEntity(TSentence, 0, BootstrapIntentBuilder.TypeMetaTypeId, PromptSource);
        b.AddEntity(TDocument, 0, BootstrapIntentBuilder.TypeMetaTypeId, PromptSource);
        await writer.ApplyAsync(b.Build(), ct);   // entities only — nothing attested
    }

    /// <summary>Decompose + compose the text, store the composed tiers (entities +
    /// CONTENT physicalities with trajectories) through the writer, and return the
    /// document entity id (the reconstruction handle).</summary>
    public static async Task<Hash128> RecordAsync(
        ISubstrateWriter writer, byte[] utf8, CancellationToken ct = default)
    {
        using var tree = TextDecomposer.Run(utf8);
        unsafe { HashComposer.Run(tree, &Resolver); }   // fills id/coord/hilbert bottom-up from the perf-cache

        int total = tree.NodeCount;
        var b = new SubstrateChangeBuilder(PromptSource, "prompt",
            parentIntentId: null, entityCapacity: total, physicalityCapacity: total);

        Hash128 documentId = Hash128.Zero;
        long observedAt = 0;
        // Content-addressing: the same grapheme/word recurs across the text but
        // is ONE entity. Emit unique n-grams only — the trajectories still
        // reference the shared ids (DAG convergence). Without this, one COPY
        // would present the same id twice → entities_pkey violation.
        var seen = new HashSet<Hash128>();

        for (uint i = 0; i < total; i++)
        {
            var v = tree.GetNode(i);
            if (v.Tier == 0) continue;   // codepoint entities are seeded; referenced, not re-emitted

            Hash128 id = v.Id;
            if (v.Tier == 4) documentId = id;   // the document root (unique)
            if (!seen.Add(id)) continue;        // already emitted this unique n-gram

            b.AddEntity(id, v.Tier, TypeForTier(v.Tier), firstObservedBy: PromptSource);

            // trajectory = ordered child entity ids. PostGIS LINESTRING needs
            // ≥2 vertices, so a single-constituent node (a 1-codepoint grapheme,
            // a 1-grapheme word) pads the lone vertex to two; n_constituents
            // (below) stays the true count and is authoritative on read.
            var children = new Hash128[v.ChildCount];
            for (uint c = 0; c < v.ChildCount; c++) children[c] = tree.GetNode(v.FirstChildIdx + c).Id;
            Hash128[] trajVerts = children.Length == 1 ? new[] { children[0], children[0] } : children;
            double[] traj = Trajectory.Build(trajVerts);

            double x, y, z, m;
            unsafe { x = v.Coord[0]; y = v.Coord[1]; z = v.Coord[2]; m = v.Coord[3]; }
            Hash128 physId = PhysicalityId.Compute(id, PromptSource, PhysicalityKind.Content, x, y, z, m, traj);

            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: id, SourceId: PromptSource, Kind: PhysicalityKind.Content,
                CoordX: x, CoordY: y, CoordZ: z, CoordM: m,
                HilbertIndex: v.Hilbert, TrajectoryXyzm: traj,
                NConstituents: (int)v.ChildCount, AlignmentResidual: 0.0, SourceDim: null,
                ObservedAtUnixUs: observedAt));

            if (v.Tier == 4) documentId = id;   // the document root
        }

        await writer.ApplyAsync(b.Build(), ct);
        return documentId;
    }

    /// <summary>Reconstruct the original bytes from the database. Scales to
    /// large content: ONE batched read pulls every composed entity's trajectory
    /// for this source into an in-memory id→children DAG (bounded by the number
    /// of UNIQUE n-grams, not by text length), then a DFS from the document
    /// walks the DAG emitting codepoints. Codepoint id→value comes from the
    /// perf-cache.</summary>
    public static async Task<byte[]> ReconstructAsync(
        NpgsqlDataSource ds, Hash128 documentId, CancellationToken ct = default)
    {
        var idToCp = new Dictionary<Hash128, uint>(1_114_112);
        ReadOnlySpan<CodepointRecord> recs = CodepointPerfcache.Records;
        for (int i = 0; i < recs.Length; i++) idToCp[recs[i].Hash] = recs[i].Codepoint;

        // One query: all (entity → ordered constituent ids) for this source.
        var nConst = new Dictionary<Hash128, int>();
        var pts = new Dictionary<Hash128, List<(int path, double x, double y, double z, double m)>>();

        await using (var conn = await ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT p.entity_id, p.n_constituents, (g.path)[1],
                       ST_X(g.geom), ST_Y(g.geom), ST_Z(g.geom), ST_M(g.geom)
                FROM laplace.physicalities p,
                     LATERAL ST_DumpPoints(p.trajectory) AS g
                WHERE p.source_id = @s AND p.kind = 1";
            cmd.Parameters.AddWithValue("s", PromptSource.ToBytes());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = ReadHash(r, 0);
                nConst[id] = r.GetInt32(1);
                if (!pts.TryGetValue(id, out var list)) pts[id] = list = new();
                list.Add((r.GetInt32(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)));
            }
        }

        // Build id → ordered children (sliced to the true constituent count).
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

        // DFS from the document (tier depth is ~5, so recursion is shallow).
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int Resolver(uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY; outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }
}
