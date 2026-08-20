using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Installed foundry/synthesis plane readers. Hosts map token slots; SQL stays here.
/// </summary>
public static class NpgsqlFoundryReads
{
    public readonly record struct WeightedEdgeRow(byte[] SubjectId, byte[] ObjectId, double W);
    public readonly record struct LayerEdgeRow(byte[] SubjectId, byte[] ObjectId, double W, double LayerRank);
    public readonly record struct TypePlaneEdgeRow(
        byte[] SubjectId, byte[] ObjectId, double W, byte[] TypeId, double LayerRank);
    public readonly record struct SurfaceWeightRow(string Surface, long Weight);
    public readonly record struct CoordRow(byte[] EntityId, double X, double Y, double Z, double M);
    /// <summary>
    /// One row of <c>generation.entity_hilbert_keys</c>. Index is the 128-bit 1D Hilbert
    /// value of the physicality centroid (collisions = shared S³ locality).
    /// </summary>
    public readonly record struct EntityHilbertKey(byte[] EntityId, Hilbert128 HilbertIndex);
    public readonly record struct GapEdgeRow(int Gap, byte[] SubjectId, byte[] ObjectId, double W);
    public readonly record struct PosRow(byte[] WordId, byte[] PosId);
    public readonly record struct HighwayMaskRow(byte[] EntityId, byte[] HighwayMask);
    public readonly record struct AttributeEdgeRow(byte[] SubjectId, byte[] NeighbourId, double W, byte[] TypeId);

    private static NpgsqlParameter ByteaArray(string name, object value) =>
        new() { ParameterName = name, Value = value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea };

    public static Task<IReadOnlyList<WeightedEdgeRow>> RelationPlaneAsync(
        NpgsqlDataSource ds, string family, string name, int? arg, byte[][]? vocab,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.relation_plane(@family, @name, @arg, @vocab)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("family", family);
                p.AddWithValue("name", name);
                p.AddWithValue("arg", (object?)arg ?? DBNull.Value);
                p.Add(ByteaArray("vocab", (object?)vocab ?? DBNull.Value));
            }, timeoutSeconds: 600, ct: ct, label: "relation_plane");

    public static Task<IReadOnlyList<WeightedEdgeRow>> EntityRelationPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, string[] relNames, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.entity_relation_plane(@vocab, @rels, @cap)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.Add(new NpgsqlParameter
                {
                    ParameterName = "rels",
                    Value = relNames,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "entity_relation_plane");

    public static Task<IReadOnlyList<LayerEdgeRow>> ConsensusLayerPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, double rankLo, double rankHi, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w, layer_rank
            FROM generation.consensus_layer_plane(@vocab, @lo, @hi, @cap)
            """,
            static r => new LayerEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2), r.GetDouble(3)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("lo", rankLo);
                p.AddWithValue("hi", rankHi);
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "consensus_layer_plane");

    public static Task<IReadOnlyList<LayerEdgeRow>> ConsensusLayerPlaneMaskedAsync(
        NpgsqlDataSource ds, byte[][] vocab, byte[] bandMask, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w, layer_rank
            FROM generation.consensus_layer_plane_masked(@vocab, @mask, @cap)
            """,
            static r => new LayerEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2), r.GetDouble(3)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.Add("mask", NpgsqlDbType.Bytea).Value = bandMask;
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "consensus_layer_plane_masked");

    public static Task<IReadOnlyList<TypePlaneEdgeRow>> ConsensusTypePlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, int degreeCap, byte[][]? typeIds,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w, type_id, layer_rank
            FROM generation.consensus_type_plane(@vocab, @cap, @types)
            """,
            static r => new TypePlaneEdgeRow(
                (byte[])r[0], (byte[])r[1], r.GetDouble(2), (byte[])r[3], r.GetDouble(4)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", degreeCap);
                p.Add(ByteaArray("types", (object?)typeIds ?? DBNull.Value));
            }, timeoutSeconds: 600, ct: ct, label: "consensus_type_plane");

    public static Task<IReadOnlyList<WeightedEdgeRow>> ConsensusAdjacencyAsync(
        NpgsqlDataSource ds, byte[][] vocab, int degreeCap, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.consensus_adjacency(@vocab, @cap)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "consensus_adjacency");

    public static Task<IReadOnlyList<WeightedEdgeRow>> MetricEdgesAsync(
        NpgsqlDataSource ds, byte[][] vocab, string metric, int k, int probe,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.metric_edges(@vocab, @metric, @k, @probe)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("metric", metric);
                p.AddWithValue("k", k);
                p.AddWithValue("probe", probe);
            }, timeoutSeconds: 0, ct: ct, label: "metric_edges");

    public static Task<IReadOnlyList<CoordRow>> EntityPhysicalityCoordsAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT entity_id, x, y, z, m FROM ops.entity_physicality_coords(@vocab)
            """,
            static r => new CoordRow(
                (byte[])r[0], r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_physicality_coords");

    public static Task<IReadOnlyList<EntityHilbertKey>> EntityHilbertKeysAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT entity_id, hilbert_index FROM generation.entity_hilbert_keys(@vocab)
            """,
            static r => new EntityHilbertKey(
                (byte[])r[0], Hilbert128.FromBytes((byte[])r[1])),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_hilbert_keys");

    public static Task<IReadOnlyList<GapEdgeRow>> EntityTrajectoryPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, int gap, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT gap, subject_id, object_id, w FROM laplace.entity_trajectory_plane(@vocab, @gap, @cap)
            """,
            static r => new GapEdgeRow(r.GetInt32(0), (byte[])r[1], (byte[])r[2], r.GetDouble(3)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("gap", gap);
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "entity_trajectory_plane");

    public static Task<IReadOnlyList<WeightedEdgeRow>> GraphemeOrderAsync(
        NpgsqlDataSource ds, byte[][] vocab, int limit, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.grapheme_order(@vocab, 50000, @limit)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("limit", limit);
            }, timeoutSeconds: 600, ct: ct, label: "grapheme_order");

    public static Task<IReadOnlyList<WeightedEdgeRow>> WordOrderAsync(
        NpgsqlDataSource ds, byte[][] vocab, int trajs, int gap,
        CancellationToken ct = default, int timeoutSeconds = 0) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.word_order(@vocab, @trajs, @gap)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("trajs", trajs);
                p.AddWithValue("gap", gap);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "word_order");

    public readonly record struct ConditionalEdgeRow(byte[] SubjectId, byte[]? ObjectId, double W);

    public static Task<IReadOnlyList<ConditionalEdgeRow>> ContinuationConditionalPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, int trajs, double smoothK, int cap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w
            FROM generation.continuation_conditional_plane(@vocab, @trajs, @smooth, @cap)
            """,
            static r => new ConditionalEdgeRow(
                (byte[])r[0], r.IsDBNull(1) ? null : (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("trajs", trajs);
                p.AddWithValue("smooth", smoothK);
                p.AddWithValue("cap", cap);
            }, timeoutSeconds: 0, ct: ct, label: "continuation_conditional_plane");

    public static Task<object?> WarmRelationTypeIdAsync(
        NpgsqlConnection conn, string canonical, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn, "SELECT laplace.relation_type_id(@c)",
            p => p.AddWithValue("c", canonical), ct: ct, label: "warm_relation_type_id");

    public static Task SetCorpusMaxRowsAsync(
        NpgsqlConnection conn, int corpusMax, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(conn, """
            SELECT set_config('laplace_substrate.corpus_max_rows', @v, false)
            """,
            p => p.AddWithValue("v", corpusMax.ToString()), ct: ct, label: "set_corpus_max_rows");

    public static Task<object?> HighwayMaskRefreshAsync(
        NpgsqlConnection conn, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn, """
            SELECT consensus.highway_mask_refresh(@vocab)
            """,
            p => p.Add(ByteaArray("vocab", vocab)), ct: ct, label: "highway_mask_refresh");

    public static Task<IReadOnlyList<PosRow>> VocabDominantPosAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT word_id, pos_id FROM generation.vocab_dominant_pos(@vocab)
            """,
            static r => new PosRow((byte[])r[0], (byte[])r[1]),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "vocab_dominant_pos");

    public static Task<IReadOnlyList<WeightedEdgeRow>> PosClassTransitionsAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.pos_class_transitions(@vocab)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "pos_class_transitions");

    public static Task<IReadOnlyList<WeightedEdgeRow>> SentenceOrderWordBridgeAsync(
        NpgsqlDataSource ds, byte[][] vocab, int degreeCap, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT subject_id, object_id, w FROM generation.sentence_order_word_bridge(@vocab, @cap)
            """,
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "sentence_order_word_bridge");

    public static Task<IReadOnlyList<HighwayMaskRow>> EntityHighwayMasksAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT entity_id, highway_mask FROM generation.entity_highway_masks(@vocab)
            """,
            static r => new HighwayMaskRow((byte[])r[0], (byte[])r[1]),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_highway_masks");

    public static Task<IReadOnlyList<SurfaceWeightRow>> GraphemeFloorVocabAsync(
        NpgsqlDataSource ds, int vocabN, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT surface, weight FROM generation.grapheme_floor_vocab(@n)
            """,
            static r => new SurfaceWeightRow(r.GetString(0), r.GetInt64(1)),
            p => p.AddWithValue("n", vocabN),
            timeoutSeconds: 0, ct: ct, label: "grapheme_floor_vocab");

    public static Task<IReadOnlyList<string>> CorpusWordVocabAsync(
        NpgsqlDataSource ds, int seedN, int wordTrajs, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT surface FROM generation.corpus_word_vocab(@n, @trajs)
            """,
            static r => r.GetString(0),
            p =>
            {
                p.AddWithValue("n", seedN);
                p.AddWithValue("trajs", wordTrajs);
            }, timeoutSeconds: 0, ct: ct, label: "corpus_word_vocab");

    public static Task<IReadOnlyList<SurfaceWeightRow>> FoundryVocabCrawlAsync(
        NpgsqlDataSource ds, string[] seeds, int vocabN, int hops, int fanout,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT surface, weight FROM generation.foundry_vocab_crawl(@seeds, @n, @hops, @fanout)
            """,
            static r => new SurfaceWeightRow(r.GetString(0), r.GetInt64(1)),
            p =>
            {
                p.Add(new NpgsqlParameter
                {
                    ParameterName = "seeds",
                    Value = seeds,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                p.AddWithValue("n", vocabN);
                p.AddWithValue("hops", hops);
                p.AddWithValue("fanout", fanout);
            }, timeoutSeconds: 0, ct: ct, label: "foundry_vocab_crawl");

    public static Task<byte[]?> SourceIdAsync(
        NpgsqlDataSource ds, string source, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(ds, "SELECT laplace.source_id(@s)",
            p => p.AddWithValue("s", source), ct: ct, label: "source_id");

    /// <summary>
    /// Surfaces for crawl-seed pinning: <c>realize.render_text(laplace.word_id(s), 80)</c> over a text array.
    /// </summary>
    public static Task<IReadOnlyList<string>> RenderResolvedWordSurfacesAsync(
        NpgsqlDataSource ds, string[] surfaces, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            SELECT realize.render_text(laplace.word_id(s), 80)
            FROM unnest(@surfaces::text[]) AS s
            WHERE laplace.word_id(s) IS NOT NULL
            """,
            static r => r.GetString(0),
            p =>
            {
                p.Add(new NpgsqlParameter
                {
                    ParameterName = "surfaces",
                    Value = surfaces,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
            }, timeoutSeconds: 120, ct: ct, label: "render_resolved_word_surfaces");

    /// <summary>
    /// Init SQL that shadows <c>laplace.consensus</c> with a source-scoped re-fold in pg_temp.
    /// </summary>
    public static string ScopedConsensusTempInitSql(IReadOnlyList<byte[]> sourceIds)
    {
        if (sourceIds.Count == 0)
            throw new ArgumentException("scoped synthesis needs at least one source_id", nameof(sourceIds));
        string arr = string.Join(",", sourceIds.Select(id =>
            $"decode('{Convert.ToHexString(id)}','hex')"));
        return
            "CREATE TEMP TABLE IF NOT EXISTS consensus AS " +
            $"SELECT * FROM consensus.scoped_consensus(ARRAY[{arr}]::bytea[]); " +
            "CREATE INDEX IF NOT EXISTS scoped_consensus_subject ON pg_temp.consensus (subject_id)";
    }

    /// <summary>
    /// Ingest datasource; when <paramref name="scopeSourceIds"/> is set, every physical
    /// connection installs the scoped consensus temp table (NoResetOnClose — pool reset
    /// would drop it and silently unscope later readers).
    /// </summary>
    public static NpgsqlDataSource CreateIngestDataSource(
        string? connString, IReadOnlyList<byte[]>? scopeSourceIds = null)
    {
        if (scopeSourceIds is null || scopeSourceIds.Count == 0)
            return LaplaceDataSource.Create(SubstrateAccess.Ingest, connString);
        string initSql = ScopedConsensusTempInitSql(scopeSourceIds);
        return LaplaceDataSource.Create(SubstrateAccess.Ingest, dsb =>
        {
            dsb.ConnectionStringBuilder.NoResetOnClose = true;
            dsb.UsePhysicalConnectionInitializer(
                conn =>
                {
                    using var c = conn.CreateCommand();
                    c.CommandText = initSql;
                    c.ExecuteNonQuery();
                },
                async conn =>
                {
                    await using var c = conn.CreateCommand();
                    c.CommandText = initSql;
                    await c.ExecuteNonQueryAsync();
                });
        }, connString);
    }

    public readonly record struct AttributeOutRow(byte[] SubjectId, byte[] NeighbourId, double W);

    /// <summary>Outbound attribute edges via consensus.edges_raw(walk_edge_weight, clamped ≥ 0).</summary>
    public static Task<IReadOnlyList<AttributeOutRow>> AttributeOutboundAsync(
        NpgsqlDataSource ds, string relationType, byte[][] vocab, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, """
            -- ONE PLAN, NOT ONE PER VOCAB ENTRY. consensus.edges_raw is LANGUAGE sql with
            -- UNION ALL + ORDER BY + LIMIT, so it does not inline: under CROSS JOIN LATERAL
            -- it re-plans against a 216-leaf partitioned table once per subject, and a
            -- foundry vocab is thousands of subjects inside the 180 s command timeout.
            -- Inlined here because both partition keys must reach the predicate: type_id is
            -- a folded IMMUTABLE literal (LIST level) and subject_id is a ScalarArrayOp
            -- (HASH level). Handing either one to a per-row variable loses the pruning.
            -- Equivalence to the edges_raw call it replaces, at THIS call site only:
            -- p_direction 'out' keeps the outbound arm; p_refuted true disables the refuted
            -- filter; a one-element p_types is an equality; the window reproduces
            -- ORDER BY eff_mu::float8/1e9 DESC, neighbour_id LIMIT p_limit per subject.
            WITH ranked AS (
                SELECT c.subject_id,
                       c.object_id AS neighbour_id,
                       GREATEST(consensus.walk_edge_weight(c.rating, c.rd), 0) AS w,
                       row_number() OVER (
                           PARTITION BY c.subject_id
                           ORDER BY (consensus.eff_mu(c.rating, c.rd))::float8 / 1e9 DESC,
                                    c.object_id
                       ) AS rn
                FROM laplace.consensus c
                WHERE c.subject_id = ANY (@vocab)
                  AND c.type_id = laplace.relation_type_id(@rel)
            )
            SELECT subject_id, neighbour_id, w
            FROM ranked
            WHERE rn <= @cap
            """,
            static r => new AttributeOutRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("rel", relationType);
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", Math.Max(0, degreeCap));
            }, timeoutSeconds: 180, ct: ct, label: "attribute_outbound");
}
