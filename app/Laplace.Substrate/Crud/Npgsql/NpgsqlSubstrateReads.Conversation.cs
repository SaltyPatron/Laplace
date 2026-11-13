using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    public readonly record struct ConverseReplyRow(string Reply, decimal? EffMu, long? Witnesses);

    private static void BindRecallSession(NpgsqlParameterCollection p, string prompt, byte[]? session)
    {
        p.AddWithValue("p", prompt);
        var sessionParam = p.Add("session", NpgsqlDbType.Bytea);
        sessionParam.Value = session is null ? DBNull.Value : session;
    }

    private static ConverseReplyRow MapConverseReply(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? "" : r.GetString(0),
        r.IsDBNull(1) ? null : r.GetDecimal(1),
        r.IsDBNull(2) ? null : r.GetInt64(2));

    /// <summary><c>converse.recall_session(prompt, session)</c> — session-resident converse.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlConnection conn, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("converse.recall_session"), MapConverseReply,
            p => BindRecallSession(p, prompt, session),
            ct: ct, label: "recall_session", onError: onError);

    /// <inheritdoc cref="RecallSessionAsync(NpgsqlConnection, string, byte[]?, CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallSessionAsync(
        NpgsqlDataSource dataSource, string prompt, byte[]? session, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.recall_session"), MapConverseReply,
            p => BindRecallSession(p, prompt, session),
            ct: ct, label: "recall_session", onError: onError);

    /// <summary><c>converse.recall_intent(shape, topic, topic2, type, lang, ctx)</c>.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallIntentAsync(
        NpgsqlDataSource dataSource, string shape, byte[] topic, byte[]? topic2,
        string? relationType, string? lang, byte[][]? contextIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.recall_intent"),
            MapConverseReply,
            p =>
            {
                p.AddWithValue("shape", shape);
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("topic2", NpgsqlDbType.Bytea).Value = (object?)topic2 ?? DBNull.Value;
                p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.Add(new NpgsqlParameter("lang", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)lang ?? DBNull.Value });
                p.Add("ctx", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
                    (object?)contextIds ?? DBNull.Value;
            }, ct: ct, label: "recall_intent", onError: onError);

    /// <summary><c>converse.recall(prompt)</c> — intent-routed converse without a session.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> RecallAsync(
        NpgsqlConnection conn, string prompt, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("converse.recall"),
            MapConverseReply,
            p => p.AddWithValue("p", prompt),
            ct: ct, label: "recall", onError: onError);

    /// <summary><c>consensus.gaps(converse.resolve_last_word(prompt))</c>.</summary>
    public static Task<IReadOnlyList<string>> GapsForPromptAsync(
        NpgsqlConnection conn, string prompt, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("consensus.gaps_for_prompt"),
            static r => r.IsDBNull(0) ? "" : r.GetString(0),
            p => p.AddWithValue("p", prompt),
            ct: ct, label: "gaps", onError: onError);

    /// <summary><c>converse.first_placed_topic(word)</c>.</summary>
    public static Task<byte[]?> FirstPlacedTopicAsync(
        NpgsqlConnection conn, string word, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(conn,
            SqlCatalog.Get("converse.first_placed_topic"),
            p => p.AddWithValue("w", word),
            ct: ct, label: "first_placed_topic", onError: onError);

    public readonly record struct CompletionRow(
        string ObjectIdHex, string TypeIdHex, decimal EffectiveMu, long Witnesses, string ObjectLabel);

    /// <summary>Ranked continuation candidates for a resolved prompt.</summary>
    public static Task<IReadOnlyList<CompletionRow>> CompletionsAsync(
        NpgsqlConnection conn, string prompt, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.completions_rendered"),
            static r => new CompletionRow(
                r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3), r.GetString(4)),
            p =>
            {
                p.AddWithValue("prompt", prompt);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "completions", onError: onError);

    public readonly record struct WalkBranchStepRow(
        int Depth, string[] PathHex, string[] TypePathHex, string EntityIdHex, string EntityLabel,
        decimal EffMu, decimal PathMu, long Witnesses);

    /// <summary><c>public.laplace_pull</c> from the prompt's Native identity — the explain trace.</summary>
    public static Task<IReadOnlyList<WalkBranchStepRow>> ExplainTraceStepsAsync(
        NpgsqlConnection conn, string prompt, int depth, int beam, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("consensus.walk_branches_rendered"),
            static r => new WalkBranchStepRow(
                r.GetInt32(0), r.GetFieldValue<string[]>(1), r.GetFieldValue<string[]>(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt64(7)),
            p =>
            {
                p.AddWithValue("prompt", prompt);
                p.AddWithValue("depth", depth);
                p.AddWithValue("beam", beam);
            }, ct: ct, label: "explain_trace_steps", onError: onError);

    public readonly record struct WalkBranchRow(
        int Depth, string Path, decimal EffMu, decimal PathMu, long Witnesses);

    /// <summary><c>public.laplace_pull</c> from the prompt's Native identity or a hex entity id.</summary>
    public static Task<IReadOnlyList<WalkBranchRow>> WalkBranchesAsync(
        NpgsqlDataSource dataSource, string? prompt, string? entityHex, string? relationType,
        int depth, int breadth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.walk_branches_for_prompt"),
            static r => new WalkBranchRow(
                r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                // Explicit types: a DBNull without an NpgsqlDbType leaves the parameter
                // type undetermined (42P08), and at least one of prompt/entity is always
                // null because they are mutually exclusive.
                p.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)prompt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)entityHex ?? DBNull.Value });
                p.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.AddWithValue("depth", depth);
                p.AddWithValue("breadth", breadth);
            }, ct: ct, label: "walk_branches", onError: onError);

    public readonly record struct QueryShapeRow(
        string Shape, string Summary, bool NeedsTopic2, bool NeedsType, bool AcceptsLang);

    /// <summary><c>converse.query_shapes()</c> — live recall_intent shape catalog.</summary>
    public static Task<IReadOnlyList<QueryShapeRow>> QueryShapesAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.query_shapes"),
            static r => new QueryShapeRow(
                r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)),
            ct: ct, label: "query_shapes", onError: onError);

    public readonly record struct RelationBandRow(
        int Band, string Name, double Rank, long RelationTypes, long ConsensusRows);

    /// <summary><c>converse.relation_bands()</c> — salience bands with live consensus counts.</summary>
    public static Task<IReadOnlyList<RelationBandRow>> RelationBandsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.relation_bands"),
            static r => new RelationBandRow(
                r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)),
            ct: ct, label: "relation_bands", onError: onError);

    /// <summary>
    /// Band-gated edges via <c>edges_raw</c> + <c>relation_band_catalog</c> (both directions).
    /// </summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> BandFactsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int[]? bands, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.band_facts"),
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                    (object?)bands ?? DBNull.Value;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "band_facts", onError: onError);

    /// <summary><c>public.laplace_pull</c>: the cheapest chain at the deepest hop.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkStrongestAsync(
        NpgsqlDataSource dataSource, byte[] topic, int depth, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.walk_strongest_rendered"),
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.AddWithValue("depth", depth);
            }, ct: ct, label: "walk_strongest", onError: onError);

    /// <summary>
    /// <c>public.laplace_pull</c>, optionally restricted to a named relation's Native middle (bands have no Native analog).
    /// </summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkBranchesBeamAsync(
        NpgsqlDataSource dataSource, byte[] topic, string? relationType, int[]? bands,
        int depth, int breadth, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("consensus.walk_branches_beam"),
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)relationType ?? DBNull.Value });
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                    (object?)bands ?? DBNull.Value;
                p.AddWithValue("depth", depth);
                p.AddWithValue("breadth", breadth);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "walk_branches", onError: onError);

    /// <summary><c>public.laplace_pull</c> from topic to topic2; directed and geometry have no Native analog.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> AstarPathAsync(
        NpgsqlDataSource dataSource, byte[] topic, byte[] topic2, int depth,
        bool directed, bool useGeometry, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.astar_path_rendered"),
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.Add("topic2", NpgsqlDbType.Bytea).Value = topic2;
                p.AddWithValue("depth", depth);
                p.AddWithValue("directed", directed);
                p.AddWithValue("geometry", useGeometry);
            }, ct: ct, label: "astar_path", onError: onError);

    /// <summary><c>generation.walk_continuations</c> — seeded trajectory descent.</summary>
    public static Task<IReadOnlyList<ConverseReplyRow>> WalkContinuationsAsync(
        NpgsqlDataSource dataSource, byte[] topic, int steps, int maxStride,
        double spread, int breadth, long? seed, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("generation.walk_continuations_rendered"),
            MapConverseReply,
            p =>
            {
                p.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                p.AddWithValue("steps", steps);
                p.AddWithValue("stride", maxStride);
                p.AddWithValue("spread", spread);
                p.AddWithValue("breadth", breadth);
                p.Add(new NpgsqlParameter("seed", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)seed ?? DBNull.Value });
            }, ct: ct, label: "walk_continuations", onError: onError);

    /// <summary>The observation holding the word nearest a sentence in length, as text; a word held by more than k observations is a hub and answers nothing.</summary>
    public static Task<string?> RecallTrajectoryAnswerAsync(
        NpgsqlDataSource dataSource, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(dataSource, SqlCatalog.Get("generation.recall_trajectory_answer"),
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "recall_trajectories", onError: onError);

    /// <summary>Top <c>consensus.salient_facts(word_id(w), NULL, k)</c> fact by eff_mu.</summary>
    public static Task<string?> SalientFactForWordAsync(
        NpgsqlDataSource dataSource, string word, int k, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<string>(dataSource, SqlCatalog.Get("consensus.salient_fact_for_word"),
            p =>
            {
                p.AddWithValue("w", word);
                p.AddWithValue("k", k);
            }, ct: ct, label: "salient_facts_word", onError: onError);
}
