using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class SubstrateClient : ISubstrateClient, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public SubstrateClient()
    {
        var connString = BuildConnectionString();
        _dataSource = new NpgsqlDataSourceBuilder(connString).Build();
    }

    internal NpgsqlDataSource DataSource => _dataSource;








    public async Task<IReadOnlyList<ConverseRow>> ConverseAsync(string prompt, byte[]? session, CancellationToken ct)
        => await ConverseTurnsAsync([prompt], session, ct);







    public async Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct)
    {











        const string sql = """
            SELECT reply, eff_mu, witnesses
            FROM laplace.recall(@p, CASE WHEN @prior = '' THEN NULL
                                         ELSE laplace.resolve_topic(@prior, NULL) END);
            """;
        try
        {
            var prompt = userTurns[^1];
            var prior = userTurns.Count >= 2 ? userTurns[^2] : "";

            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", prompt);
            cmd.Parameters.AddWithValue("prior", prior);

            var rows = new List<ConverseRow>(8);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var reply = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var mu = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                var witnesses = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                rows.Add(new ConverseRow(reply, mu, witnesses));
            }
            return rows;
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"recall query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }








    public async IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const string sql =
            "SELECT step, entity, stride_used FROM laplace.walk_text(@p, @steps, @order, @temp, @topk);";
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
            var tok = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var ord = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            if (tok.Length == 0) continue;


            yield return new GenerateToken(step, tok, ord);
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
                laplace.label_or_hex(c.object_id) AS object_label
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

            var nodeIds = new byte[nodes.Length][];
            for (int i = 0; i < nodes.Length; i++)
                nodeIds[i] = Convert.FromHexString(nodes[i].id);

            // ONE round-trip: first physicality (lowest type) per node, keyed by array ordinal.
            var geometry = new (double X, double Y, double Z, double M, double Radius, int Constituents)?[nodes.Length];
            if (includeGeometry && nodes.Length > 0)
            {
                const string physSql = """
                    SELECT u.ord, f.x, f.y, f.z, f.m, f.radius, f.n_constituents
                    FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
                    JOIN LATERAL (
                        SELECT x, y, z, m, radius, n_constituents
                        FROM laplace.entity_physicalities(u.id)
                        ORDER BY type
                        LIMIT 1
                    ) f ON true;
                    """;
                await using var cmd = new NpgsqlCommand(physSql, conn);
                var p = cmd.Parameters.AddWithValue("ids", nodeIds);
                p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    int idx = (int)reader.GetInt64(0) - 1;
                    geometry[idx] = (
                        reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3),
                        reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6));
                }
            }

            // ONE round-trip: evidence count per node over the same array.
            var evidence = new long?[nodes.Length];
            if (includeEvidence && nodes.Length > 0)
            {
                const string evSql = """
                    SELECT u.ord, laplace.evidence_count(NULL, NULL, u.id)
                    FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord);
                    """;
                await using var cmd = new NpgsqlCommand(evSql, conn);
                var p = cmd.Parameters.AddWithValue("ids", nodeIds);
                p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    int idx = (int)reader.GetInt64(0) - 1;
                    evidence[idx] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            var output = new List<VisualizationNode>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++)
            {
                var physicality = geometry[i];
                output.Add(new VisualizationNode(
                    IdHex: nodes[i].id,
                    Label: nodes[i].label,
                    X: physicality?.X,
                    Y: physicality?.Y,
                    Z: physicality?.Z,
                    M: physicality?.M,
                    Radius: physicality?.Radius,
                    Constituents: physicality?.Constituents,
                    EvidenceRows: evidence[i]));
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
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.types) AS u(x)) AS type_path_hex,
                encode(gt.entity_id, 'hex') AS entity_id_hex,
                laplace.label_or_hex(gt.entity_id) AS entity_label,
                gt.eff_mu,
                gt.path_mu,
                gt.witnesses
            FROM laplace.walk_branches(laplace.word_id(@prompt), NULL, @depth, @beam) gt
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
                        TypePathHex: reader.GetFieldValue<string[]>(2),
                        EntityIdHex: reader.GetString(3),
                        EntityLabel: reader.GetString(4),
                        EffectiveMu: reader.GetDecimal(5),
                        PathMu: reader.GetDecimal(6),
                        Witnesses: reader.GetInt64(7),
                        Evidence: Array.Empty<EvidenceSample>()));
                }
            }

            if (!includeEvidence || rows.Count == 0)
                return rows;

            // ONE round-trip: batch evidence for every step's entity via LATERAL attestations_out,
            // then bucket client-side. Distinct ids collapse the (frequently repeated) entity_ids.
            var distinctHex = rows.Select(r => r.EntityIdHex).Distinct(StringComparer.Ordinal).ToArray();
            var ids = new byte[distinctHex.Length][];
            for (int i = 0; i < distinctHex.Length; i++)
                ids[i] = Convert.FromHexString(distinctHex[i]);

            const string evSql = """
                SELECT u.ord,
                    encode(a.type_id, 'hex'),
                    encode(a.object_id, 'hex'),
                    encode(a.source_id, 'hex'),
                    CASE WHEN a.context_id IS NULL THEN NULL ELSE encode(a.context_id, 'hex') END,
                    a.outcome,
                    a.observation_count
                FROM unnest(@ids::bytea[]) WITH ORDINALITY AS u(id, ord)
                CROSS JOIN LATERAL laplace.attestations_out(u.id, 5)
                    WITH ORDINALITY AS a(type_id, object_id, source_id, context_id, outcome, observation_count, aord)
                ORDER BY u.ord, a.aord;
                """;
            var buckets = new Dictionary<string, List<EvidenceSample>>(StringComparer.Ordinal);
            await using (var evCmd = new NpgsqlCommand(evSql, conn))
            {
                var p = evCmd.Parameters.AddWithValue("ids", ids);
                p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                await using var reader = await evCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var hex = distinctHex[(int)reader.GetInt64(0) - 1];
                    if (!buckets.TryGetValue(hex, out var list))
                    {
                        list = new List<EvidenceSample>();
                        buckets[hex] = list;
                    }
                    list.Add(new EvidenceSample(
                        TypeIdHex: reader.GetString(1),
                        ObjectIdHex: reader.GetString(2),
                        SourceIdHex: reader.GetString(3),
                        ContextIdHex: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Outcome: reader.GetInt16(5),
                        ObservationCount: reader.GetInt64(6)));
                }
            }

            var enriched = new List<ExplainTraceStep>(rows.Count);
            foreach (var row in rows)
            {
                IReadOnlyList<EvidenceSample> evidence =
                    buckets.TryGetValue(row.EntityIdHex, out var list) ? list : Array.Empty<EvidenceSample>();
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
                laplace.label_or_hex(t.subject_id) AS subject_label,
                encode(t.type_id, 'hex') AS type_id_hex,
                laplace.label_or_hex(t.type_id) AS type_label,
                encode(t.object_id, 'hex') AS object_id_hex,
                laplace.label_or_hex(t.object_id) AS object_label,
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




    public async Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct)
    {
        // ONE round-trip: resolve CTE feeds both the entity label and the evidence payload.
        // LEFT JOIN LATERAL keeps a single anchor row when there is no evidence (resolved-but-empty)
        // and when @target does not resolve (entity_id NULL -> caller returns null).
        const string sql = """
            WITH resolved AS (
                SELECT laplace.resolve_ref(@target) AS id
            )
            SELECT
                r.id,
                laplace.label_or_hex(r.id),
                encode(a.type_id, 'hex'),
                laplace.label_or_hex(a.type_id),
                encode(a.object_id, 'hex'),
                laplace.label_or_hex(a.object_id),
                encode(a.source_id, 'hex'),
                laplace.label_or_hex(a.source_id),
                CASE WHEN a.context_id IS NULL THEN NULL ELSE encode(a.context_id, 'hex') END,
                a.outcome,
                a.observation_count
            FROM resolved r
            LEFT JOIN LATERAL laplace.attestations_out(r.id, @limit)
                WITH ORDINALITY AS a(type_id, object_id, source_id, context_id, outcome, observation_count, aord)
                ON true
            ORDER BY a.aord;
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId = null;
            string? entityLabel = null;
            var items = new List<Laplace.Api.Contracts.LabeledEvidenceItem>(limit);

            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("target", target.Trim());
                cmd.Parameters.AddWithValue("limit", limit);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (entityId is null && !reader.IsDBNull(0))
                    {
                        entityId = (byte[])reader[0];
                        entityLabel = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                    if (reader.IsDBNull(2))
                        continue; // anchor row with no evidence
                    items.Add(new Laplace.Api.Contracts.LabeledEvidenceItem(
                        TypeId: reader.GetString(2),
                        TypeLabel: reader.GetString(3),
                        ObjectId: reader.GetString(4),
                        ObjectLabel: reader.GetString(5),
                        SourceId: reader.GetString(6),
                        SourceLabel: reader.GetString(7),
                        ContextId: reader.IsDBNull(8) ? null : reader.GetString(8),
                        Outcome: reader.GetInt16(9),
                        ObservationCount: reader.GetInt64(10)));
                }
            }

            if (entityId is null)
                return null;

            return new EntityEvidence(Convert.ToHexStringLower(entityId), entityLabel!, items);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"evidence query failed [{pg.SqlState}] {pg.MessageText}", pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate evidence query failed.", ex);
        }
    }





    public async Task<ReadinessResponse> ReadinessAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            long entities = 0, consensus = 0;
            await using (var cmd = new NpgsqlCommand("SELECT metric, value FROM laplace.substrate_counts();", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    switch (reader.GetString(0))
                    {
                        case "entities": entities = reader.GetInt64(1); break;
                        case "consensus (relations)": consensus = reader.GetInt64(1); break;
                    }
                }
            }




            bool perfcacheReady;
            string? detail = null;
            try
            {
                await using var probe = new NpgsqlCommand("SELECT laplace.word_id('the');", conn);
                await probe.ExecuteScalarAsync(ct);
                perfcacheReady = true;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState)
            {
                perfcacheReady = false;
                detail = pg.MessageText;
            }

            var ready = entities > 0 && consensus > 0 && perfcacheReady;
            if (ready)
                return new ReadinessResponse(true, true, entities, consensus, true);

            detail ??= entities == 0 ? "substrate has no entities (unseeded)"
                : consensus == 0 ? "substrate has no consensus relations (unseeded)"
                : "T0 perfcache not loaded";
            return new ReadinessResponse(false, true, entities, consensus, perfcacheReady, detail);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new ReadinessResponse(false, false, 0, 0, false, $"substrate unreachable: {ex.Message}");
        }
    }



    public async Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct)
    {
        // ONE round-trip: resolve CTE feeds both the physical form (kind=0 anchor row) and the
        // meaning neighbors (kind=1 rows, gated by @include). ORDER BY kind, ord preserves the
        // form-then-meaning read order and consensus_out_readable's internal ranking.
        const string sql = """
            WITH resolved AS (
                SELECT laplace.resolve_ref(@target) AS id
            )
            SELECT 0 AS kind, 0::bigint AS ord, r.id AS eid,
                   f.x, f.y, f.z, f.m, f.radius, f.n_constituents,
                   NULL::text, NULL::text, NULL::numeric, NULL::bigint
            FROM resolved r
            LEFT JOIN LATERAL (
                SELECT x, y, z, m, radius, n_constituents
                FROM laplace.entity_physicalities(r.id)
                ORDER BY type
                LIMIT 1
            ) f ON true
            UNION ALL
            SELECT 1 AS kind, m.ord, r.id,
                   NULL::float8, NULL::float8, NULL::float8, NULL::float8, NULL::float8, NULL::int,
                   m.type, m.object, m.eff_mu, m.witnesses
            FROM resolved r
            CROSS JOIN LATERAL laplace.consensus_out_readable(r.id, @limit)
                WITH ORDINALITY AS m(type, object, eff_mu, witnesses, ord)
            WHERE @include
            ORDER BY kind, ord;
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId = null;
            EmbeddingForm? form = null;
            var meaning = new List<MeaningNeighbor>();

            await using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("target", input.Trim());
                cmd.Parameters.AddWithValue("limit", Math.Clamp(meaningLimit, 1, 100));
                cmd.Parameters.AddWithValue("include", includeMeaning);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.GetInt32(0) == 0)
                    {
                        entityId = reader.IsDBNull(2) ? null : (byte[])reader[2];
                        if (!reader.IsDBNull(3))
                            form = new EmbeddingForm(
                                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                                reader.GetDouble(6), reader.GetDouble(7), reader.GetInt32(8));
                    }
                    else
                    {
                        meaning.Add(new MeaningNeighbor(
                            Relation: reader.IsDBNull(9) ? "?" : reader.GetString(9),
                            ObjectLabel: reader.IsDBNull(10) ? "?" : reader.GetString(10),
                            EffMu: reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                            Witnesses: reader.IsDBNull(12) ? 0L : reader.GetInt64(12)));
                    }
                }
            }

            if (entityId is null)
                return new EmbeddingResult(null, null, Array.Empty<MeaningNeighbor>());

            return new EmbeddingResult(Convert.ToHexStringLower(entityId), form, meaning);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"embedding query failed [{pg.SqlState}] {pg.MessageText}", pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate embedding query failed.", ex);
        }
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






internal sealed class SubstrateQueryException : Exception
{
    public SubstrateQueryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
