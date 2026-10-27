using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Set reads over the installed <c>generation.*</c> planes that model export composes from
/// current standing: consensus planes, trajectory order, geometry keys and highway masks.
/// Callers assign vocabulary slots; the planes are computed in the extension.
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
    /// One row of <c>generation.entity_hilbert_keys</c>: the 128-bit Hilbert index of the
    /// entity's coordinate physicality. Equal indexes mean shared S³ locality, not identity.
    /// </summary>
    public readonly record struct EntityHilbertKey(byte[] EntityId, Hilbert128 HilbertIndex);
    public readonly record struct GapEdgeRow(int Gap, byte[] SubjectId, byte[] ObjectId, double W);
    public readonly record struct PosRow(byte[] WordId, byte[] PosId);
    public readonly record struct HighwayMaskRow(byte[] EntityId, byte[] HighwayMask);
    public readonly record struct AttributeEdgeRow(byte[] SubjectId, byte[] NeighbourId, double W, byte[] TypeId);

    public readonly record struct PathEdgeRow(byte[] SubjectId, byte[] ObjectId, double W, string Paths);

    public static async Task<IReadOnlyList<PathEdgeRow>> PathPlaneAsync(
        NpgsqlDataSource ds, byte[][] roots, string stepsJson, int fanout, int cap, bool visibleOnly)
    {
        await using var connection=await ds.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var scope=NpgsqlCatalog.Command(connection,transaction,"generation.path_search_path"))
            await scope.ExecuteNonQueryAsync();
        await using var command=NpgsqlCatalog.Command(connection,transaction,"generation.path_plane",roots,stepsJson,fanout,cap,visibleOnly);
        command.CommandTimeout=600;
        await using var reader=await command.ExecuteReaderAsync();
        var rows=new List<PathEdgeRow>();
        while(await reader.ReadAsync()) rows.Add(new((byte[])reader[0],(byte[])reader[1],reader.GetDouble(2),reader.GetString(3)));
        return rows;
    }

    public readonly record struct OrderedSequenceRow(byte[] EntityId,byte[] PhysicalityId,byte[][] Constituents);
    public static async Task<IReadOnlyList<OrderedSequenceRow>> ExportSequencesAsync(NpgsqlDataSource ds,byte[][] vocab,int limit,int contextLength,byte[][]? sources=null)
    {
        await using var connection=await ds.OpenConnectionAsync();
        await using var command=NpgsqlCatalog.Command(connection,null,"generation.export_sequences",vocab,limit,contextLength,(object?)sources??DBNull.Value);
        command.CommandTimeout=600;await using var reader=await command.ExecuteReaderAsync();var rows=new List<OrderedSequenceRow>();
        while(await reader.ReadAsync())rows.Add(new((byte[])reader[0],(byte[])reader[1],reader.GetFieldValue<byte[][]>(2)));
        return rows;
    }

    public readonly record struct SourceObservationRow(byte[] SourceId,byte[] ClaimId,long Games,long Rating,long Rd,long OpponentRating,long OpponentRd);
    public static async Task<IReadOnlyList<SourceObservationRow>> ExportSourceObservationsAsync(NpgsqlDataSource ds,string touched,byte[][]? sources=null)
    {
        await using var connection=await ds.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
        await using(var scope=NpgsqlCatalog.Command(connection,transaction,"generation.path_search_path"))await scope.ExecuteNonQueryAsync();
        await using var command=NpgsqlCatalog.Command(connection,transaction,"generation.export_source_observations",touched,(object?)sources??DBNull.Value);
        command.CommandTimeout=600;await using var reader=await command.ExecuteReaderAsync();var rows=new List<SourceObservationRow>();
        while(await reader.ReadAsync())rows.Add(new((byte[])reader[0],(byte[])reader[1],reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6)));
        return rows;
    }

    private static NpgsqlParameter ByteaArray(string name, object value) =>
        new() { ParameterName = name, Value = value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea };

    public static Task<IReadOnlyList<WeightedEdgeRow>> RelationPlaneAsync(
        NpgsqlDataSource ds, string family, string name, int? arg, byte[][]? vocab,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.relation_plane"),
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("family", family);
                p.AddWithValue("name", name);
                p.Add("arg", NpgsqlDbType.Integer).Value = (object?)arg ?? DBNull.Value;
                p.Add(ByteaArray("vocab", (object?)vocab ?? DBNull.Value));
            }, timeoutSeconds: 600, ct: ct, label: "relation_plane");

    public static Task<IReadOnlyList<WeightedEdgeRow>> EntityRelationPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, string[] relNames, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.entity_relation_plane"),
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
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.consensus_layer_plane"),
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
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.consensus_layer_plane_masked"),
            static r => new LayerEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2), r.GetDouble(3)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.Add("mask", NpgsqlDbType.Bytea).Value = bandMask;
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "consensus_layer_plane_masked");

    public static async Task<IReadOnlyList<TypePlaneEdgeRow>> ConsensusTypePlaneAsync(
        NpgsqlDataSource ds,byte[][] vocab,int degreeCap,byte[][]? typeIds,CancellationToken ct=default)
    {
        await using var connection=await ds.OpenConnectionAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var scope=NpgsqlCatalog.Command(connection,transaction,"generation.path_search_path"))await scope.ExecuteNonQueryAsync(ct);
        await using var command=NpgsqlCatalog.Command(connection,transaction,"generation.export_type_plane",vocab,degreeCap,(object?)typeIds??DBNull.Value);
        command.CommandTimeout=600;await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<TypePlaneEdgeRow>();
        while(await reader.ReadAsync(ct))rows.Add(new((byte[])reader[0],(byte[])reader[1],reader.GetDouble(2),(byte[])reader[3],reader.GetDouble(4)));
        return rows;
    }

    public static async Task<IReadOnlyList<WeightedEdgeRow>> ConsensusAdjacencyAsync(NpgsqlDataSource ds,byte[][] vocab,int degreeCap,CancellationToken ct=default)
    {
        await using var connection=await ds.OpenConnectionAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var scope=NpgsqlCatalog.Command(connection,transaction,"generation.path_search_path"))await scope.ExecuteNonQueryAsync(ct);
        await using var command=NpgsqlCatalog.Command(connection,transaction,"generation.export_adjacency",vocab,degreeCap);
        command.CommandTimeout=600;await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<WeightedEdgeRow>();
        while(await reader.ReadAsync(ct))rows.Add(new((byte[])reader[0],(byte[])reader[1],reader.GetDouble(2)));
        return rows;
    }

    public static Task<IReadOnlyList<WeightedEdgeRow>> MetricEdgesAsync(
        NpgsqlDataSource ds, byte[][] vocab, string metric, int k, int probe,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.metric_edges"),
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
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("ops.entity_physicality_coords"),
            static r => new CoordRow(
                (byte[])r[0], r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_physicality_coords");

    public static Task<IReadOnlyList<EntityHilbertKey>> EntityHilbertKeysAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.entity_hilbert_keys"),
            static r => new EntityHilbertKey(
                (byte[])r[0], Hilbert128.FromBytes((byte[])r[1])),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_hilbert_keys");

    public static Task<IReadOnlyList<GapEdgeRow>> EntityTrajectoryPlaneAsync(
        NpgsqlDataSource ds, byte[][] vocab, int gap, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("laplace.entity_trajectory_plane"),
            static r => new GapEdgeRow(r.GetInt32(0), (byte[])r[1], (byte[])r[2], r.GetDouble(3)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("gap", gap);
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "entity_trajectory_plane");

    public static Task<IReadOnlyList<WeightedEdgeRow>> GraphemeOrderAsync(
        NpgsqlDataSource ds, byte[][] vocab, int limit, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.grapheme_order"),
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("limit", limit);
            }, timeoutSeconds: 600, ct: ct, label: "grapheme_order");

    public static Task<IReadOnlyList<WeightedEdgeRow>> WordOrderAsync(
        NpgsqlDataSource ds, byte[][] vocab, int trajs, int gap,
        CancellationToken ct = default, int timeoutSeconds = 0) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.word_order"),
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
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.continuation_conditional_plane"),
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
        NpgsqlRead.ExecuteScalarAsync<object>(conn, SqlCatalog.Get("foundry.warm_relation_type_id"),
            p => p.AddWithValue("c", canonical), ct: ct, label: "warm_relation_type_id");

    public static Task<object?> HighwayMaskRefreshAsync(
        NpgsqlConnection conn, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteScalarAsync<object>(conn, SqlCatalog.Get("foundry.highway_mask_refresh"),
            p => p.Add(ByteaArray("vocab", vocab)), ct: ct, label: "highway_mask_refresh");

    public static Task<IReadOnlyList<PosRow>> VocabDominantPosAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.vocab_dominant_pos"),
            static r => new PosRow((byte[])r[0], (byte[])r[1]),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "vocab_dominant_pos");

    public static Task<IReadOnlyList<WeightedEdgeRow>> PosClassTransitionsAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.pos_class_transitions"),
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "pos_class_transitions");

    public static Task<IReadOnlyList<WeightedEdgeRow>> SentenceOrderWordBridgeAsync(
        NpgsqlDataSource ds, byte[][] vocab, int degreeCap, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.sentence_order_word_bridge"),
            static r => new WeightedEdgeRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", degreeCap);
            }, timeoutSeconds: 600, ct: ct, label: "sentence_order_word_bridge");

    public static Task<IReadOnlyList<HighwayMaskRow>> EntityHighwayMasksAsync(
        NpgsqlDataSource ds, byte[][] vocab, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.entity_highway_masks"),
            static r => new HighwayMaskRow((byte[])r[0], (byte[])r[1]),
            p => p.Add(ByteaArray("vocab", vocab)),
            timeoutSeconds: 600, ct: ct, label: "entity_highway_masks");

    public static Task<IReadOnlyList<SurfaceWeightRow>> GraphemeFloorVocabAsync(
        NpgsqlDataSource ds, int vocabN, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.grapheme_floor_vocab"),
            static r => new SurfaceWeightRow(r.GetString(0), r.GetInt64(1)),
            p => p.AddWithValue("n", vocabN),
            timeoutSeconds: 0, ct: ct, label: "grapheme_floor_vocab");

    public static Task<IReadOnlyList<string>> CorpusWordVocabAsync(
        NpgsqlDataSource ds, int seedN, int wordTrajs, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.corpus_word_vocab"),
            static r => r.GetString(0),
            p =>
            {
                p.AddWithValue("n", seedN);
                p.AddWithValue("trajs", wordTrajs);
            }, timeoutSeconds: 0, ct: ct, label: "corpus_word_vocab");

    public static Task<IReadOnlyList<SurfaceWeightRow>> FoundryVocabCrawlAsync(
        NpgsqlDataSource ds, string[] seeds, int vocabN, int hops, int fanout,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("generation.foundry_vocab_crawl"),
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
        NpgsqlRead.ExecuteScalarAsync<byte[]>(ds, SqlCatalog.Get("foundry.source_id"),
            p => p.AddWithValue("s", source), ct: ct, label: "source_id");

    /// <summary>
    /// Realized surfaces of the inputs that resolve to a word entity; unresolved inputs are
    /// dropped.
    /// </summary>
    public static Task<IReadOnlyList<string>> RenderResolvedWordSurfacesAsync(
        NpgsqlDataSource ds, string[] surfaces, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("realize.resolved_word_surfaces"),
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
    /// Ingest datasource; when <paramref name="scopeSourceIds"/> is set, every physical
    /// connection installs the source-scoped consensus temp table. NoResetOnClose keeps the
    /// pool reset from dropping it, which would silently unscope later readers.
    /// </summary>
    public static NpgsqlDataSource CreateIngestDataSource(
        string? connString, IReadOnlyList<byte[]>? scopeSourceIds = null)
    {
        if (scopeSourceIds is null || scopeSourceIds.Count == 0)
            return LaplaceDataSource.Create(SubstrateAccess.Ingest, connString);
        // The datasource retains a fixed scope for every physical connection.
        // Snapshot the caller's buffers before the initializer captures them.
        var sources = new byte[scopeSourceIds.Count][];
        for (int i = 0; i < sources.Length; i++)
        {
            var id = scopeSourceIds[i];
            if (id is null || id.Length != 16)
                throw new ArgumentException("Source IDs must contain exactly 16 bytes.", nameof(scopeSourceIds));
            sources[i] = (byte[])id.Clone();
        }
        return LaplaceDataSource.Create(SubstrateAccess.Ingest, dsb =>
        {
            dsb.ConnectionStringBuilder.NoResetOnClose = true;
            dsb.UsePhysicalConnectionInitializer(
                conn =>
                {
                    using var c = NpgsqlCatalog.Command(conn, null, "foundry.initialize_scope", sources);
                    c.ExecuteNonQuery();
                    using var index = NpgsqlCatalog.Command(conn, null, "foundry.index_scope");
                    index.ExecuteNonQuery();
                },
                async conn =>
                {
                    await using var c = NpgsqlCatalog.Command(conn, null, "foundry.initialize_scope", sources);
                    await c.ExecuteNonQueryAsync();
                    await using var index = NpgsqlCatalog.Command(conn, null, "foundry.index_scope");
                    await index.ExecuteNonQueryAsync();
                });
        }, connString);
    }

    public readonly record struct AttributeOutRow(byte[] SubjectId, byte[] NeighbourId, double W);

    /// <summary>Outbound consensus edges of one relation type per vocabulary subject, ranked by
    /// effective rating and capped at <paramref name="degreeCap"/>; weight is
    /// <c>walk_edge_weight</c> clamped at zero.</summary>
    public static Task<IReadOnlyList<AttributeOutRow>> AttributeOutboundAsync(
        NpgsqlDataSource ds, string relationType, byte[][] vocab, int degreeCap,
        CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("foundry.attribute_outbound"),
            static r => new AttributeOutRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("rel", relationType);
                p.Add(ByteaArray("vocab", vocab));
                p.AddWithValue("cap", Math.Max(0, degreeCap));
            }, timeoutSeconds: 180, ct: ct, label: "attribute_outbound");
}
