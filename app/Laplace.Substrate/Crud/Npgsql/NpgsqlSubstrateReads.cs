using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Typed readers over the installed substrate operations. Every surface (web, API, MCP,
/// CLI) reads through these, so one operation has one statement and one row shape;
/// consumers name the operation and receive rows.
///
/// Every method takes an optional <see cref="NpgsqlRead.ErrorTranslator"/> that maps
/// database failures into the caller's own exception types.
/// </summary>
public static partial class NpgsqlSubstrateReads
{
    internal static int RequestedLimit(int limit) => Math.Max(0, limit);

    /// <summary><c>laplace.is_compositional_type(type_id)</c></summary>
    public static async Task<bool> IsCompositionalTypeAsync(
        NpgsqlDataSource dataSource, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.is_compositional_type"),
            static r => !r.IsDBNull(0) && r.GetBoolean(0),
            p => p.AddWithValue("type", typeId),
            ct: ct, label: "is_compositional_type", onError: onError).ConfigureAwait(false);
        return rows.Count > 0 && rows[0];
    }

    /// <summary><c>ops.top_relations_readable(limit, type)</c></summary>
    public static Task<IReadOnlyList<(string Subject, string Type, string Object, decimal EffMu, long Witnesses)>> TopRelationsReadableAsync(
        NpgsqlDataSource dataSource, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.top_relations_readable"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? 0m : r.GetDecimal(3),
                r.IsDBNull(4) ? 0L : r.GetInt64(4)),
            p => p.AddWithValue("limit", limit),
            ct: ct, label: "top_relations_readable", onError: onError);

    /// <summary><c>consensus.relation_rank(type_id)</c> — highway rank weight.</summary>
    public static async Task<double> RelationRankAsync(
        NpgsqlDataSource dataSource, byte[] typeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.relation_rank"),
            static r => r.IsDBNull(0) ? 0d : r.GetDouble(0),
            p => p.AddWithValue("type", typeId),
            ct: ct, label: "relation_rank", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0d : rows[0];
    }

    /// <summary><c>consensus.effective_mu(rating, rd)</c> — integer μ−2·RD.</summary>
    public static async Task<long> EffectiveMuAsync(
        NpgsqlDataSource dataSource, long rating, long rd, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.effective_mu"),
            static r => r.IsDBNull(0) ? 0L : r.GetInt64(0),
            p =>
            {
                p.AddWithValue("rating", rating);
                p.AddWithValue("rd", rd);
            }, ct: ct, label: "effective_mu", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary><c>consensus.relation_type_resolve(surface)</c> — name/alias → type id.</summary>
    public static async Task<byte[]?> RelationTypeResolveAsync(
        NpgsqlDataSource dataSource, string surface, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.relation_type_resolve"),
            static r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0),
            p => p.AddWithValue("surface", surface),
            ct: ct, label: "relation_type_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>laplace.intent_preflight(entity_ids, phys_ids, att_ids)</c></summary>
    public static async Task<byte[]?> IntentPreflightEntityBitmapAsync(
        NpgsqlDataSource dataSource, byte[][] entityIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.intent_preflight_entities"),
            static r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0),
            p => p.AddWithValue("entities", entityIds),
            ct: ct, label: "intent_preflight", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary><c>consensus.relation_family_type_ids(family)</c> / <c>relation_band_types(band)</c>.</summary>
    public static async Task<byte[][]> RelationFamilyTypeIdsAsync(
        NpgsqlDataSource dataSource, string family, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.relation_family_type_ids"),
            static r => r.IsDBNull(0) ? Array.Empty<byte[]>() : r.GetFieldValue<byte[][]>(0),
            p => p.AddWithValue("family", family),
            ct: ct, label: "relation_family_type_ids", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? [] : rows[0];
    }

    /// <summary><c>laplace.laplace_score(v, m)</c> / <c>laplace_score_inverse(score, m)</c>.</summary>
    public static async Task<(long Score, double Inverse)> LaplaceScorePairAsync(
        NpgsqlDataSource dataSource, double v, double m, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("laplace.score_pair"),
            static r => (r.IsDBNull(0) ? 0L : r.GetInt64(0), r.IsDBNull(1) ? 0d : r.GetDouble(1)),
            p =>
            {
                p.AddWithValue("v", v);
                p.AddWithValue("m", m);
            }, ct: ct, label: "laplace_score", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0L, 0d) : rows[0];
    }

    public readonly record struct BandLeaderRow(
        int Band, string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    /// <summary><c>ops.band_leaders(bands, per_band)</c> — top edges per salience band.</summary>
    public static Task<IReadOnlyList<BandLeaderRow>> BandLeadersAsync(
        NpgsqlDataSource dataSource, int[] bands, int perBand, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("leaders.page"),
            static r => new BandLeaderRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.AddWithValue("per", perBand);
            }, ct: ct, label: "band_leaders", onError: onError);

    /// <summary><c>ops.entity_record(id)</c> — confirmed/contested/refuted/thin edge counts.</summary>
    public static async Task<(long Confirmed, long Contested, long Refuted, long Thin)> EntityRecordAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("ops.entity_record"),
            static r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_record", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (0, 0, 0, 0) : rows[0];
    }

    public readonly record struct SalientFactRow(string Type, string Fact, decimal EffMu, long Witnesses);

    /// <summary>
    /// <c>consensus.salient_facts(id, relation_type, limit)</c> — an entity's typed consensus
    /// relations ranked by effective rating, with fallback to <c>ops.consensus_out_readable</c>
    /// when no synset-band facts exist.
    /// </summary>
    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.salient_facts_rendered"),
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public readonly record struct TapeRow(string Holder, string Type, string Fact, decimal? Mu);

    /// <summary><c>converse.contrast(x, y, relation_type, limit)</c> — the head-to-head tape.</summary>
    public static Task<IReadOnlyList<TapeRow>> ContrastAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.contrast"),
            static r => new TapeRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3)),
            p =>
            {
                p.Add("x", NpgsqlDbType.Bytea).Value = x;
                p.Add("y", NpgsqlDbType.Bytea).Value = y;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "contrast", onError: onError);

    public readonly record struct RelationSummaryRow(
        string? Relation, string? Plane, decimal? Mu, long? Usage, double? Geodesic, string? Verdict);

    /// <summary>
    /// <c>consensus.relation_summary(x, y)</c> — relation, plane, standing, geodesic and
    /// verdict between two entities. Path search makes this slow, so it runs with a
    /// 120-second timeout instead of the default serving budget.
    /// </summary>
    public static async Task<RelationSummaryRow?> RelationSummaryAsync(
        NpgsqlDataSource dataSource, byte[] x, byte[] y, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.relation_summary"),
            static r => new RelationSummaryRow(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetString(5)),
            p =>
            {
                p.Add("x", NpgsqlDbType.Bytea).Value = x;
                p.Add("y", NpgsqlDbType.Bytea).Value = y;
            }, timeoutSeconds: 120, ct: ct, label: "relation_summary", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Connection overload of <c>consensus.salient_facts</c>, same statement as the
    /// datasource overload, for callers reading several facets on one connection.
    /// </summary>
    public static Task<IReadOnlyList<SalientFactRow>> SalientFactsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.salient_facts_rendered"),
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "salient_facts", onError: onError);

    public readonly record struct ConsensusOutLabeledRow(
        string TypeLabel, string ObjectLabel, string ObjectIdHex, decimal EffMu, long Witnesses);

    /// <summary><c>ops.consensus_out_labeled(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<ConsensusOutLabeledRow>> ConsensusOutLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.out_labeled"),
            static r => new ConsensusOutLabeledRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "consensus_out_labeled", onError: onError);

    public readonly record struct ConsensusOutReadableRow(
        long Ordinal, string Type, string? Object, decimal EffMu, long Witnesses);

    /// <summary>
    /// Batch <c>ops.consensus_out_readable(id, limit)</c> over an id array, keyed by ordinal.
    /// </summary>
    public static Task<IReadOnlyList<ConsensusOutReadableRow>> ConsensusOutReadableBatchAsync(
        NpgsqlConnection conn, byte[][] ids, int perId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.consensus_out_readable_batch"),
            static r => new ConsensusOutReadableRow(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.AddWithValue("lim", perId);
            }, ct: ct, label: "consensus_out_readable_batch", onError: onError);

    public readonly record struct ConsensusEdgeDisplayRow(
        string TypeLabel, string? PeerLabel, long Rating, long Rd, long Volatility, long WitnessCount);

    /// <summary><c>consensus.consensus_out(id)</c> with bounded display labels after edge election.</summary>
    public static Task<IReadOnlyList<ConsensusEdgeDisplayRow>> ConsensusOutDisplayAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.consensus_out"),
            static r => new ConsensusEdgeDisplayRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "consensus_out_display", onError: onError);

    /// <summary><c>consensus.consensus_in(id)</c> with bounded display labels after edge election.</summary>
    public static Task<IReadOnlyList<ConsensusEdgeDisplayRow>> ConsensusInDisplayAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.consensus_in"),
            static r => new ConsensusEdgeDisplayRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "consensus_in_display", onError: onError);

    public readonly record struct TopRelationEdgeRow(
        string SubjectIdHex, string Subject, string TypeIdHex, string Type,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    /// <summary>
    /// Top-k consensus edges by salience band and effective rating. Ranking and limiting
    /// happen in <c>consensus.top_relations</c>; this statement only labels the result,
    /// realizing all endpoint labels in one <c>realize.batch</c> call after the limit
    /// (rank, then label).
    /// </summary>
    public static Task<IReadOnlyList<TopRelationEdgeRow>> TopRelationsAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.top_relations_rendered"),
            static r => new TopRelationEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetInt64(7)),
            p => p.AddWithValue("limit", limit), ct: ct, label: "top_relations", onError: onError);

    public readonly record struct ConsensusInLabeledRow(
        string SubjectIdHex, string TypeLabel, string SubjectLabel, decimal EffMu, long Witnesses);

    /// <summary><c>consensus.consensus_in(id, limit)</c> — inbound consensus cells with labeled subjects.</summary>
    public static Task<IReadOnlyList<ConsensusInLabeledRow>> ConsensusInLabeledAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        // The shared native display pipeline bounds high-tier previews and renders
        // source-defined relation content as well as its subjects, in one batch.
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.in_labeled"),
            static r => new ConsensusInLabeledRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "consensus_in_labeled", onError: onError);

    public readonly record struct EdgesRawRow(
        string Direction, byte[] TypeId, byte[] NeighbourId,
        long Rating, long Rd, long WitnessCount);

    /// <summary>
    /// <c>consensus.edges_raw(subject, direction, types, limit, refuted, rank)</c> — the
    /// installed consensus edge scan around one subject (labeled form:
    /// <c>consensus.edges()</c>).
    /// </summary>
    public static Task<IReadOnlyList<EdgesRawRow>> EdgesRawAsync(
        NpgsqlDataSource dataSource, byte[] subject,
        string direction = "both", byte[][]? types = null, int limit = 40,
        bool refuted = false, string rank = "edge_rank",
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.edges_raw"),
            static r => new EdgesRawRow(
                r.GetString(0), (byte[])r[1], (byte[])r[2],
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)),
            p =>
            {
                p.Add("subject", NpgsqlDbType.Bytea).Value = subject;
                p.AddWithValue("direction", direction);
                var typesParam = p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea);
                typesParam.Value = (object?)types ?? DBNull.Value;
                p.AddWithValue("limit", limit);
                p.AddWithValue("refuted", refuted);
                p.AddWithValue("rank", rank);
            }, ct: ct, label: "edges_raw", onError: onError);

    public readonly record struct BestOutboundRow(
        byte[] SubjectId, byte[] ObjectId, decimal EffMu, long WitnessCount);

    /// <summary>
    /// Strongest outbound edge of one type per subject: <c>consensus.edges_raw</c> with
    /// limit 1 ranked by effective rating, run for every subject in one statement.
    /// </summary>
    public static Task<IReadOnlyList<BestOutboundRow>> BestOutboundBySubjectsAsync(
        NpgsqlDataSource dataSource, byte[][] subjects, byte[] typeId,
        bool refuted = true, int timeoutSeconds = 120,
        CancellationToken ct = default, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.best_outbound_by_subjects"),
            static r => new BestOutboundRow(
                (byte[])r[0], (byte[])r[1], r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                var ids = p.AddWithValue("ids", subjects);
                ids.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
                p.AddWithValue("refuted", refuted);
            }, timeoutSeconds: timeoutSeconds, ct: ct,
            label: "edges_raw_best_outbound", onError: onError);
}
