using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    public readonly record struct MeshPositionRow(
        string Dir, string IdHex, string Label, string Relation, string? HubType,
        decimal? EffMu, long Witnesses);

    /// <summary><c>structural.mesh_position(id)</c> — hub gating and ranking live in the extension.</summary>
    public static Task<IReadOnlyList<MeshPositionRow>> MeshPositionAsync(
        NpgsqlDataSource dataSource, byte[] id, int relationLimit, int memberLimit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("structural.mesh_position"),
            static r => new MeshPositionRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                r.IsDBNull(6) ? 0L : r.GetInt64(6)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("relation_limit", RequestedLimit(relationLimit));
                p.AddWithValue("member_limit", RequestedLimit(memberLimit));
            },
            ct: ct, label: "mesh_position", onError: onError);

    public readonly record struct Neighbor4dRow(string Neighbor, double Geodesic, double? Frechet);

    /// <summary><c>structural.nearest_neighbors_4d(word, k)</c>.</summary>
    public static Task<IReadOnlyList<Neighbor4dRow>> NearestNeighbors4dAsync(
        NpgsqlConnection conn, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("structural.nearest_neighbors_4d"),
            static r => new Neighbor4dRow(
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0 : r.GetDouble(1),
                r.IsDBNull(2) ? null : r.GetDouble(2)),
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "nearest_neighbors_4d", onError: onError);

    public readonly record struct ExploreResolveRow(byte[] Id, string Label, string RefKind, bool Exists);

    /// <summary>Resolves a concept, word, or hexadecimal entity reference for the explorer.</summary>
    public static async Task<ExploreResolveRow?> ExploreResolveAsync(
        NpgsqlConnection conn, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.explore_resolve"),
            static r => new ExploreResolveRow(
                r.GetFieldValue<byte[]>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            p => BindCanonicalReference(p, reference.Trim()),
            ct: ct, label: "explore_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct AnchorNeighborRow(
        string Axis, string IdHex, string? Label, short? Tier, double? Geodesic, double? Frechet);

    /// <summary>Nearest stored entities to a computed explorer anchor.</summary>
    public static Task<IReadOnlyList<AnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        NpgsqlConnection conn, double cx, double cy, double cz, double cm, string? trajectoryWkt,
        int geodesicK, int frechetK, double frechetMax, int timeoutSeconds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("structural.explore_anchor_neighbors"),
            static r => new AnchorNeighborRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt16(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5)),
            p =>
            {
                p.AddWithValue("cx", cx);
                p.AddWithValue("cy", cy);
                p.AddWithValue("cz", cz);
                p.AddWithValue("cm", cm);
                p.Add(new NpgsqlParameter("traj", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)trajectoryWkt ?? DBNull.Value });
                p.AddWithValue("gk", geodesicK);
                p.AddWithValue("fk", frechetK);
                p.AddWithValue("fmax", frechetMax);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_anchor_neighbors", onError: onError);

    public readonly record struct WitnessedWordRow(string Surface, string IdHex, long Witnesses);

    /// <summary>Returns candidate word surfaces that resolve to witnessed entities.</summary>
    public static Task<IReadOnlyList<WitnessedWordRow>> WitnessedWordsAsync(
        NpgsqlConnection conn, string[] surfaces, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.witnessed_words"),
            static r => new WitnessedWordRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p =>
            {
                var parameter = p.AddWithValue("surfaces", surfaces);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            }, ct: ct, label: "witnessed_words", onError: onError);

    public readonly record struct StructuralNeighborRow(
        string? IdHex, string? Label, double Geodesic, double? Frechet,
        double? X, double? Y, double? Z, double? M, double? Radius);

    /// <summary>Structural neighbours for one stored entity.</summary>
    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("structural.neighbors_of"),
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("k", limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);

    public readonly record struct ContainerRow(string IdHex, short Tier, string Type, int Hops, string Label);

    public static async Task<IReadOnlyList<ContainerRow>> ContainersAsync(
        NpgsqlConnection conn, byte[] id, int hops, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("containers.page"),
            static r => (Id: r.GetFieldValue<byte[]>(0), Tier: r.GetInt16(1),
                Type: r.GetFieldValue<byte[]>(2), Hops: r.GetInt32(3)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("hops", hops);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "containers", onError: onError);
        if (rows.Count == 0) return [];
        var types = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("types.labels"),
            static r => r.GetFieldValue<string[]>(0),
            p => p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = rows.Select(r => r.Type).ToArray(),
            ct: ct, label: "container_types", onError: onError);
        return rows.Select((r, i) => new ContainerRow(
            Convert.ToHexString(r.Id).ToLowerInvariant(), r.Tier, types[0][i], r.Hops, "")).ToArray();
    }

    public readonly record struct EmbeddingLookupRow(
        int Kind, long Ord, byte[]? EntityId,
        double? X, double? Y, double? Z, double? M, double? Radius, int? Constituents,
        string? Relation, string? ObjectLabel, decimal? EffMu, long? Witnesses);

    /// <summary>
    /// One round trip: the resolve CTE feeds both the physical form (kind=0 anchor row)
    /// and the meaning neighbors (kind=1 rows, gated by <paramref name="includeMeaning"/>).
    /// Includes synset salient facts, concept peers, and constituent-word expansion for multi-word inputs.
    /// </summary>
    public static Task<IReadOnlyList<EmbeddingLookupRow>> EmbeddingLookupAsync(
        NpgsqlConnection conn, string target, int meaningLimit, bool includeMeaning, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null, string[]? constituentTerms = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.embedding_lookup"),
            static r => new EmbeddingLookupRow(
                r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? null : (byte[])r[2],
                r.IsDBNull(3) ? null : r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDecimal(11),
                r.IsDBNull(12) ? null : r.GetInt64(12)),
            p =>
            {
                p.AddWithValue("target", target);
                p.AddWithValue("limit", meaningLimit);
                p.AddWithValue("include", includeMeaning);
                var termsParam = p.Add("terms", NpgsqlDbType.Array | NpgsqlDbType.Text);
                termsParam.Value = (object?)constituentTerms ?? DBNull.Value;
            }, ct: ct, label: "embedding_lookup", onError: onError);

    /// <summary><c>render_text_fast</c> with a <c>label_or_hex</c> fallback for one id.</summary>
    public static Task<string?> LabelOrHexAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(conn, SqlCatalog.Get("realize.label_or_hex"),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "label_or_hex", onError: onError);

    public readonly record struct ExploreWebEdgeRow(
        string SourceIdHex, string TypeIdHex, string ObjectIdHex, short Hop, decimal EffMu,
        long WitnessCount, double CompleteWeight, bool Refuted);

    /// <summary>
    /// <c>consensus.explore_web</c> — native sparse star expansion from <paramref name="seed"/>
    /// over consensus in both directions, up to <paramref name="hops"/> hops, at most
    /// <paramref name="fanout"/> new nodes per frontier parent and <paramref name="maxNodes"/>
    /// in total.
    /// </summary>
    public static Task<IReadOnlyList<ExploreWebEdgeRow>> ExploreWebAsync(
        NpgsqlConnection conn, byte[] seed, int hops, int fanout, int maxNodes, int timeoutSeconds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.explore_web_rendered"),
            static r => new ExploreWebEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt16(3), r.GetDecimal(4),
                r.GetInt64(5), r.GetDouble(6), r.GetBoolean(7)),
            p =>
            {
                p.Add("seed", NpgsqlDbType.Bytea).Value = seed;
                p.AddWithValue("hops", hops);
                p.AddWithValue("fanout", fanout);
                p.AddWithValue("max_nodes", maxNodes);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_web", onError: onError);

    public readonly record struct ExploreInducedEdgeRow(
        string SourceIdHex, string TypeIdHex, string ObjectIdHex,
        decimal Rating, decimal Rd, decimal Volatility, decimal EffMu,
        long WitnessCount, double CompleteWeight, bool Refuted);

    /// <summary>Exact induced testimony graph over an already bounded node set.</summary>
    public static Task<IReadOnlyList<ExploreInducedEdgeRow>> ExploreInducedEdgesAsync(
        NpgsqlConnection conn, byte[][] nodes, int timeoutSeconds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.explore_induced_edges_rendered"),
            static r => new ExploreInducedEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                r.GetInt64(7), r.GetDouble(8), r.GetBoolean(9)),
            p =>
            {
                var param = p.AddWithValue("nodes", nodes);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_induced_edges", onError: onError);

    public readonly record struct FastLabelRow(string IdHex, string? Label, short? Tier);

    /// <summary>
    /// Label and tier for a batch of ids in one round trip. Labels come from canonical
    /// names, then resolved names, then rendered text for the tier &lt;= 3 subset, rendered
    /// in one batch. Higher-tier compositions are named, never reconstructed, here.
    /// </summary>
    public static Task<IReadOnlyList<FastLabelRow>> LabelsFastAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("realize.labels_fast"),
            static r => new FastLabelRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "labels_fast", onError: onError);

    /// <inheritdoc cref="StructuralNeighborsAsync(NpgsqlConnection, byte[], int, CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("structural.neighbors_of"),
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("k", limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);
}
