using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class SubstrateClient : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public SubstrateClient()
    {
        var connString = BuildConnectionString();
        _dataSource = new NpgsqlDataSourceBuilder(connString).Build();
    }

    /// <summary>
    /// Call the substrate's own conversational engine (<c>laplace.converse</c>), which routes the
    /// prompt via <c>route_prompt</c> and grounds every reply line in witnessed consensus
    /// (definitions, synonyms, relations, walks). Returns every reply row in order — converse
    /// emits multiple rows for compound answers (e.g. definition + hypernym chain).
    /// Session keeps pronoun/topic continuity across turns.
    /// </summary>
    public async Task<IReadOnlyList<ConverseRow>> ConverseAsync(string prompt, byte[]? session, CancellationToken ct)
        => await ConverseTurnsAsync([prompt], session, ct);

    /// <summary>
    /// Replay an ordered list of user turns through <c>laplace.converse</c> under a single session,
    /// returning the reply rows of the FINAL turn. Prior turns seed converse_turns so the
    /// substrate resolves follow-up pronouns ("…and its synonyms?") against the running topic —
    /// correct even for stateless OpenAI clients (Roo Code) that resend the full history each call.
    /// </summary>
    public async Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct)
    {
        const string sql = "SELECT reply, eff_mu, witnesses FROM laplace.converse(@p, @s);";
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            // A single connection = a single backend, so converse's session memory is consistent
            // across the replay even when @s is NULL (it falls back to the backend pid).
            var rows = new List<ConverseRow>(8);
            for (int turn = 0; turn < userTurns.Count; turn++)
            {
                bool isLast = turn + 1 == userTurns.Count;
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("p", userTurns[turn]);
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Bytea)
                    { Value = (object?)session ?? DBNull.Value });

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!isLast) continue; // prior turns only seed context
                    var reply = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var mu = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                    var witnesses = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                    rows.Add(new ConverseRow(reply, mu, witnesses));
                }
            }
            return rows;
        }
        catch (PostgresException pg)
        {
            // A SQL-level error (undefined column/function, type mismatch, …) is a query/schema
            // bug, NOT the database being unreachable. Surface the real SqlState + message so it
            // is never again mistaken for a segfault or "db unavailable".
            throw new SubstrateQueryException(
                $"converse query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }

    /// <summary>
    /// True autoregressive forward pass: resolve the prompt to a context window of entity IDs,
    /// then walk PRECEDES arcs step by step, yielding one token per step.
    /// Each yield fires before the next SQL call — this genuinely streams.
    /// Works for both text and code (no HAS_POS requirement).
    /// </summary>
    public async IAsyncEnumerable<GenerateToken> GenerateStreamAsync(
        string prompt,
        int steps       = 64,
        int window      = 4,
        double temperature = 0.8,
        int topK        = 12,
        string[]? stop  = null,
        double boost    = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Resolve prompt text → ordered array of entity IDs (last `window` entities)
        var ctx = await ResolveContextAsync(conn, prompt, window, ct);
        if (ctx.Length == 0) yield break;

        var stopIds = new HashSet<byte[]>(ByteArrayComparer.Instance);
        if (stop is { Length: > 0 })
        {
            foreach (var s in stop)
            {
                var sid = await ResolveWordIdAsync(conn, s, ct);
                if (sid != null) stopIds.Add(sid);
            }
        }

        var seen = new HashSet<byte[]>(ctx, ByteArrayComparer.Instance);

        for (int step = 1; step <= steps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var next = await PickNextTokenAsync(conn, ctx, seen, stopIds, topK, temperature, boost, ct);
            if (next == null) yield break;

            yield return new GenerateToken(step, next.Value.Label, next.Value.Mu);

            seen.Add(next.Value.Id);
            // Advance sliding window
            ctx = ctx.Length < window
                ? [.. ctx, next.Value.Id]
                : [.. ctx[1..], next.Value.Id];
        }
    }

    /// <summary>
    /// Higher-order generation via longest-context back-off over the witnessed content
    /// trajectories (<c>laplace.generate_ngram</c>): each token is reproduced verbatim while its
    /// full preceding context is attested, then recombined when the exact span runs out. No model,
    /// no HAS_POS gate. Tokens are yielded with a leading space so the OpenAI stream detokenizes to
    /// readable prose (content trajectories drop whitespace, so we re-insert the separator).
    /// </summary>
    public async IAsyncEnumerable<GenerateToken> GenerateNgramStreamAsync(
        string prompt,
        int steps          = 32,
        int maxOrder       = 5,
        double temperature = 0.7,
        int topK           = 10,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const string sql =
            "SELECT step, token, ord_used FROM laplace.generate_ngram(@p, @steps, @order, @temp, @topk);";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", prompt);
        cmd.Parameters.AddWithValue("steps", steps);
        cmd.Parameters.AddWithValue("order", maxOrder);
        cmd.Parameters.AddWithValue("temp", temperature);
        cmd.Parameters.AddWithValue("topk", topK);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var step = reader.GetInt32(0);
            var tok  = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var ord  = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            if (tok.Length == 0) continue;
            yield return new GenerateToken(step, " " + tok, ord);
        }
    }

    // ── forward-pass internals ────────────────────────────────────────────────

    private static async Task<byte[][]> ResolveContextAsync(
        NpgsqlConnection conn, string prompt, int window, CancellationToken ct)
    {
        const string sql = """
            SELECT id FROM laplace.prompt_state(@p) WHERE id IS NOT NULL ORDER BY ord DESC LIMIT @w;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", prompt);
        cmd.Parameters.AddWithValue("w", window);
        var ids = new List<byte[]>(window);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetFieldValue<byte[]>(0));
        ids.Reverse(); // oldest → newest for context ordering
        return ids.ToArray();
    }

    private static async Task<byte[]?> ResolveWordIdAsync(
        NpgsqlConnection conn, string word, CancellationToken ct)
    {
        const string sql = "SELECT laplace.word_id(@w);";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("w", word);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (byte[])v;
    }

    private static readonly Random _rng = new();

    private static async Task<(byte[] Id, string Label, decimal Mu)?> PickNextTokenAsync(
        NpgsqlConnection conn,
        byte[][] ctx,
        HashSet<byte[]> seen,
        HashSet<byte[]> stopIds,
        int topK,
        double temperature,
        double boost,
        CancellationToken ct)
    {
        // Fetch top-K PRECEDES candidates from the current context window.
        // No HAS_POS requirement — works for code tokens too.
        const string sql = """
            SELECT c.object_id,
                   COALESCE(laplace.render_text(c.object_id, 32),
                            laplace.label(c.object_id),
                            encode(c.object_id, 'hex')) AS label,
                   laplace.eff_mu_display(max(c.rating), max(c.rd))  AS mu,
                   sum(laplace.eff_mu(c.rating, c.rd))               AS sc
            FROM laplace.consensus c
            WHERE c.type_id    =  laplace.relation_type_id('PRECEDES')
              AND c.subject_id =  ANY(@ctx)
              AND c.object_id  IS NOT NULL
              AND NOT laplace.refuted(c.rating, c.rd)
            GROUP BY c.object_id
            ORDER BY sum(laplace.eff_mu(c.rating, c.rd)) DESC
            LIMIT @topk;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ctx", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea, ctx);
        cmd.Parameters.AddWithValue("topk", topK);

        var candidates = new List<(byte[] Id, string Label, decimal Mu, double Score)>(topK);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id    = reader.GetFieldValue<byte[]>(0);
            var label = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var mu    = reader.GetDecimal(2);
            var sc    = (double)reader.GetDecimal(3);
            if (seen.Contains(id)) continue;
            if (stopIds.Contains(id)) return null;
            if (string.IsNullOrEmpty(label)) continue;
            candidates.Add((id, label, mu, sc));
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return (candidates[0].Id, candidates[0].Label, candidates[0].Mu);

        // Temperature sampling: Gumbel-max trick
        // score_i → weight_i = sc_i^(1/T), then sample proportionally.
        if (temperature <= 0)
        {
            var best = candidates[0];
            return (best.Id, best.Label, best.Mu);
        }

        double[] weights = new double[candidates.Count];
        double wsum = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            double sc = candidates[i].Score;
            if (boost > 0) sc *= 1 + boost;
            weights[i] = Math.Pow(Math.Max(sc, 1e-12), 1.0 / temperature);
            wsum += weights[i];
        }

        double r = _rng.NextDouble() * wsum;
        for (int i = 0; i < candidates.Count; i++)
        {
            r -= weights[i];
            if (r <= 0)
                return (candidates[i].Id, candidates[i].Label, candidates[i].Mu);
        }
        var fallback = candidates[^1];
        return (fallback.Id, fallback.Label, fallback.Mu);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) =>
            x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var h = new HashCode();
            h.AddBytes(obj);
            return h.ToHashCode();
        }
    }

    public async Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT
                encode(c.object_id, 'hex') AS object_id_hex,
                encode(c.type_id, 'hex') AS type_id_hex,
                c.eff_mu,
                c.witnesses,
                COALESCE(laplace.label(c.object_id), encode(c.object_id, 'hex')) AS object_label
            FROM laplace.completions(laplace.word_id(@prompt), @limit) c
            ORDER BY c.eff_mu DESC;
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));

            var rows = new List<CompletionRow>(Math.Max(1, limit));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new CompletionRow(
                    ObjectIdHex: reader.GetString(0),
                    TypeIdHex: reader.GetString(1),
                    EffectiveMu: reader.GetDecimal(2),
                    Witnesses: reader.GetInt64(3),
                    ObjectLabel: reader.GetString(4)));
            }

            return rows;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate completions query failed.", ex);
        }
    }

    public async Task<SubstrateAuditReport> AuditReportAsync(bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var counts = new List<SubstrateCount>();
            await using (var cmd = new NpgsqlCommand("SELECT metric, value FROM laplace.substrate_counts();", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    counts.Add(new SubstrateCount(reader.GetString(0), reader.GetInt64(1)));
            }

            ConsensusHealth? consensus = null;
            if (includeConsensus)
            {
                await using var cmd = new NpgsqlCommand("SELECT evidence_rows, consensus_rows, dedup_ratio, avg_witnesses, max_witnesses FROM laplace.consensus_stats();", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    consensus = new ConsensusHealth(
                        EvidenceRows: reader.GetInt64(0),
                        ConsensusRows: reader.GetInt64(1),
                        DedupRatio: reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                        AvgWitnesses: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                        MaxWitnesses: reader.IsDBNull(4) ? null : reader.GetInt64(4));
                }
            }

            long? multiSource = null;
            if (includeConvergence)
            {
                await using var cmd = new NpgsqlCommand("SELECT laplace.multi_source_entity_count();", conn);
                var value = await cmd.ExecuteScalarAsync(ct);
                multiSource = value is null or DBNull ? null : Convert.ToInt64(value);
            }

            var topRelations = await ReadTopRelationsAsync(conn, Math.Clamp(topRelationLimit, 1, 200), ct);
            return new SubstrateAuditReport(counts, consensus, multiSource, topRelations);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate audit query failed.", ex);
        }
    }

    public async Task<SubstrateVisualizationGraph> VisualizationGraphAsync(int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var edges = await ReadTopRelationsAsync(conn, Math.Clamp(limit, 1, 500), ct);
            var nodes = edges
                .SelectMany(edge => new[]
                {
                    (id: edge.SubjectIdHex, label: edge.Subject),
                    (id: edge.ObjectIdHex, label: edge.Object)
                })
                .GroupBy(n => n.id, StringComparer.OrdinalIgnoreCase)
                .Select(g => (id: g.Key, label: g.First().label))
                .ToArray();

            var output = new List<VisualizationNode>(nodes.Length);
            foreach (var node in nodes)
            {
                var physicality = includeGeometry
                    ? await ReadFirstPhysicalityAsync(conn, node.id, ct)
                    : null;
                var evidenceRows = includeEvidence
                    ? await ReadEvidenceCountForObjectAsync(conn, node.id, ct)
                    : null;

                output.Add(new VisualizationNode(
                    IdHex: node.id,
                    Label: node.label,
                    X: physicality?.X,
                    Y: physicality?.Y,
                    Z: physicality?.Z,
                    M: physicality?.M,
                    Radius: physicality?.Radius,
                    Constituents: physicality?.Constituents,
                    EvidenceRows: evidenceRows));
            }

            return new SubstrateVisualizationGraph(output, edges);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate visualization query failed.", ex);
        }
    }

    public async Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct)
    {
        const string sql = """
            SELECT
                gt.depth,
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.path) AS u(x)) AS path_hex,
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.kinds) AS u(x)) AS kind_path_hex,
                encode(gt.entity_id, 'hex') AS entity_id_hex,
                COALESCE(laplace.label(gt.entity_id), encode(gt.entity_id, 'hex')) AS entity_label,
                gt.eff_mu,
                gt.path_mu,
                gt.witnesses
            FROM laplace.generate_tree(laplace.word_id(@prompt), NULL, @depth, @beam) gt
            ORDER BY gt.depth, gt.path_mu DESC;
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("depth", Math.Clamp(depth, 1, 64));
            cmd.Parameters.AddWithValue("beam", Math.Clamp(beam, 1, 64));

            var rows = new List<ExplainTraceStep>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new ExplainTraceStep(
                        Depth: reader.GetInt32(0),
                        PathHex: reader.GetFieldValue<string[]>(1),
                        KindPathHex: reader.GetFieldValue<string[]>(2),
                        EntityIdHex: reader.GetString(3),
                        EntityLabel: reader.GetString(4),
                        EffectiveMu: reader.GetDecimal(5),
                        PathMu: reader.GetDecimal(6),
                        Witnesses: reader.GetInt64(7),
                        Evidence: Array.Empty<EvidenceSample>()));
                }
            }

            if (!includeEvidence)
                return rows;

            var enriched = new List<ExplainTraceStep>(rows.Count);
            foreach (var row in rows)
            {
                var evidence = await ReadEvidenceSamplesAsync(conn, row.EntityIdHex, 5, ct);
                enriched.Add(row with { Evidence = evidence });
            }

            return enriched;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate explainability trace query failed.", ex);
        }
    }

    private static async Task<IReadOnlyList<VisualizationEdge>> ReadTopRelationsAsync(NpgsqlConnection conn, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT
                encode(t.subject_id, 'hex') AS subject_id_hex,
                COALESCE(laplace.label(t.subject_id), encode(t.subject_id, 'hex')) AS subject_label,
                encode(t.type_id, 'hex') AS type_id_hex,
                COALESCE(laplace.label(t.type_id), encode(t.type_id, 'hex')) AS type_label,
                encode(t.object_id, 'hex') AS object_id_hex,
                COALESCE(laplace.label(t.object_id), encode(t.object_id, 'hex')) AS object_label,
                t.eff_mu,
                t.witnesses
            FROM laplace.top_relations(@limit, NULL) t;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        var edges = new List<VisualizationEdge>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new VisualizationEdge(
                SubjectIdHex: reader.GetString(0),
                Subject: reader.GetString(1),
                TypeIdHex: reader.GetString(2),
                Type: reader.GetString(3),
                ObjectIdHex: reader.GetString(4),
                Object: reader.GetString(5),
                EffectiveMu: reader.GetDecimal(6),
                Witnesses: reader.GetInt64(7)));
        }

        return edges;
    }

    private static async Task<(double X, double Y, double Z, double M, double Radius, int Constituents)?> ReadFirstPhysicalityAsync(NpgsqlConnection conn, string entityIdHex, CancellationToken ct)
    {
        const string sql = """
            SELECT x, y, z, m, radius, n_constituents
            FROM laplace.entity_physicalities(decode(@id, 'hex'))
            ORDER BY type
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", entityIdHex);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.GetDouble(4),
            reader.GetInt32(5));
    }

    private static async Task<long?> ReadEvidenceCountForObjectAsync(NpgsqlConnection conn, string entityIdHex, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT laplace.evidence_count(NULL, NULL, decode(@id, 'hex'));", conn);
        cmd.Parameters.AddWithValue("id", entityIdHex);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<EvidenceSample>> ReadEvidenceSamplesAsync(NpgsqlConnection conn, string entityIdHex, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT
                encode(type_id, 'hex'),
                encode(object_id, 'hex'),
                encode(source_id, 'hex'),
                CASE WHEN context_id IS NULL THEN NULL ELSE encode(context_id, 'hex') END,
                outcome,
                observation_count
            FROM laplace.attestations_out(decode(@id, 'hex'), @limit);
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", entityIdHex);
        cmd.Parameters.AddWithValue("limit", limit);
        var samples = new List<EvidenceSample>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new EvidenceSample(
                TypeIdHex: reader.GetString(0),
                ObjectIdHex: reader.GetString(1),
                SourceIdHex: reader.GetString(2),
                ContextIdHex: reader.IsDBNull(3) ? null : reader.GetString(3),
                Outcome: reader.GetInt16(4),
                ObservationCount: reader.GetInt64(5)));
        }

        return samples;
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private static string BuildConnectionString()
    {
        var s = Environment.GetEnvironmentVariable("LAPLACE_DB")
            ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace-dev";
        if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
            s += ";Include Error Detail=true";
        if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
            s += ";Search Path=laplace,public";
        return s;
    }
}

internal sealed class SubstrateUnavailableException : Exception
{
    public SubstrateUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A SQL-level failure (undefined column/function, type mismatch, etc.) raised while executing a
/// substrate query. Distinct from <see cref="SubstrateUnavailableException"/>: the database is up;
/// the query or schema is wrong. Surfaced to the client verbatim so the real cause is visible.
/// </summary>
internal sealed class SubstrateQueryException : Exception
{
    public SubstrateQueryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
