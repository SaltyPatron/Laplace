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
                ARRAY(SELECT encode(x, 'hex') FROM unnest(gt.types) AS u(x)) AS type_path_hex,
                encode(gt.entity_id, 'hex') AS entity_id_hex,
                COALESCE(laplace.label(gt.entity_id), encode(gt.entity_id, 'hex')) AS entity_label,
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





    public async Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct)
    {
        const string resolveSql = """
            SELECT CASE
                WHEN @target ~ '^[0-9a-f]{32}$' THEN decode(@target, 'hex')
                ELSE laplace.word_id(@target)
            END;
            """;
        const string evidenceSql = """
            SELECT
                encode(a.type_id, 'hex'),
                COALESCE(laplace.label(a.type_id), encode(a.type_id, 'hex')),
                encode(a.object_id, 'hex'),
                COALESCE(laplace.label(a.object_id), encode(a.object_id, 'hex')),
                encode(a.source_id, 'hex'),
                COALESCE(laplace.label(a.source_id), encode(a.source_id, 'hex')),
                CASE WHEN a.context_id IS NULL THEN NULL ELSE encode(a.context_id, 'hex') END,
                a.outcome,
                a.observation_count
            FROM laplace.attestations_out(@id, @limit) a;
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId;
            await using (var resolve = new NpgsqlCommand(resolveSql, conn))
            {
                resolve.Parameters.AddWithValue("target", target.Trim());
                entityId = await resolve.ExecuteScalarAsync(ct) as byte[];
            }
            if (entityId is null)
                return null;

            string entityLabel;
            await using (var label = new NpgsqlCommand(
                "SELECT COALESCE(laplace.label(@id), encode(@id, 'hex'));", conn))
            {
                label.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bytea) { Value = entityId });
                entityLabel = (string)(await label.ExecuteScalarAsync(ct))!;
            }

            var items = new List<Laplace.Api.Contracts.LabeledEvidenceItem>(limit);
            await using (var cmd = new NpgsqlCommand(evidenceSql, conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bytea) { Value = entityId });
                cmd.Parameters.AddWithValue("limit", limit);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new Laplace.Api.Contracts.LabeledEvidenceItem(
                        TypeId: reader.GetString(0),
                        TypeLabel: reader.GetString(1),
                        ObjectId: reader.GetString(2),
                        ObjectLabel: reader.GetString(3),
                        SourceId: reader.GetString(4),
                        SourceLabel: reader.GetString(5),
                        ContextId: reader.IsDBNull(6) ? null : reader.GetString(6),
                        Outcome: reader.GetInt16(7),
                        ObservationCount: reader.GetInt64(8)));
                }
            }

            return new EntityEvidence(Convert.ToHexStringLower(entityId), entityLabel, items);
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
        const string resolveSql = """
            SELECT CASE
                WHEN @target ~ '^[0-9a-f]{32}$' THEN decode(@target, 'hex')
                ELSE laplace.word_id(@target)
            END;
            """;
        const string formSql = """
            SELECT x, y, z, m, radius, n_constituents
            FROM laplace.entity_physicalities(@id)
            ORDER BY type
            LIMIT 1;
            """;
        const string meaningSql = """
            SELECT type, object, eff_mu, witnesses
            FROM laplace.consensus_out_readable(@id, @limit);
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId;
            await using (var resolve = new NpgsqlCommand(resolveSql, conn))
            {
                resolve.Parameters.AddWithValue("target", input.Trim());
                entityId = await resolve.ExecuteScalarAsync(ct) as byte[];
            }
            if (entityId is null)
                return new EmbeddingResult(null, null, Array.Empty<MeaningNeighbor>());

            EmbeddingForm? form = null;
            await using (var cmd = new NpgsqlCommand(formSql, conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bytea) { Value = entityId });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    form = new EmbeddingForm(
                        reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2),
                        reader.GetDouble(3), reader.GetDouble(4), reader.GetInt32(5));
            }

            var meaning = new List<MeaningNeighbor>();
            if (includeMeaning)
            {
                await using var cmd = new NpgsqlCommand(meaningSql, conn);
                cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bytea) { Value = entityId });
                cmd.Parameters.AddWithValue("limit", Math.Clamp(meaningLimit, 1, 100));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    meaning.Add(new MeaningNeighbor(
                        Relation: reader.IsDBNull(0) ? "?" : reader.GetString(0),
                        ObjectLabel: reader.IsDBNull(1) ? "?" : reader.GetString(1),
                        EffMu: reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                        Witnesses: reader.IsDBNull(3) ? 0L : reader.GetInt64(3)));
            }

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
