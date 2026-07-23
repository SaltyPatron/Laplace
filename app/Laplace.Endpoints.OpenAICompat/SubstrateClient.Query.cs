using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The structural read surface. Every dial the native functions accept is a
/// parameter here — the previous surface accepted the same arguments and then
/// pinned them to constants in C#, which is why the app could only ever ask for
/// one shape of read.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>Read shapes, straight from the substrate's own catalog.</summary>
    public async Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT shape, summary, needs_topic2, needs_type, accepts_lang
            FROM laplace.query_shapes()
            """;
        return await ReadRowsAsync(sql, static r => new QueryShape(
            r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)),
            static _ => { }, "query_shapes", ct);
    }

    /// <summary>Salience bands with live consensus counts.</summary>
    public async Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT band, name, rank, relation_types, consensus_rows
            FROM laplace.relation_bands()
            """;
        return await ReadRowsAsync(sql, static r => new RelationBand(
            r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)),
            static _ => { }, "relation_bands", ct);
    }

    /// <summary>Resolve a word or a 32-hex id to a content id, with its label.</summary>
    public async Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id, laplace.label_or_hex(r.id)
            FROM laplace.resolve_ref(@ref) r(id)
            WHERE r.id IS NOT NULL
            """;
        var rows = await ReadRowsAsync(sql,
            static r => ((byte[])r[0], r.IsDBNull(1) ? string.Empty : r.GetString(1)),
            cmd => cmd.Parameters.AddWithValue("ref", reference),
            "resolve_ref", ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// A shape-dispatched read. Shapes the responder family covers go through
    /// recall_intent; the walk, path and generation shapes go to their native
    /// entry points with the caller's dials applied.
    /// </summary>
    public async Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct)
    {
        switch (shape)
        {
            case "band_facts":
                return await BandFactsAsync(topic, bands, dials.Limit, lang, ct);
            case "beam":
                return await BeamAsync(topic, relationType, bands, dials, ct);
            case "path":
                return await PathAsync(topic, topic2, dials, ct);
            case "neighbors":
                return await GeometricNeighborsAsync(topic, dials.Limit, ct);
            case "generate":
                return await GenerateAsync(topic, dials, ct);
            default:
                return await RecallIntentAsync(shape, topic, topic2, relationType, lang, contextIds, ct);
        }
    }

    private Task<IReadOnlyList<QueryRow>> RecallIntentAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, CancellationToken ct)
    {
        const string sql = """
            SELECT reply, eff_mu, witnesses
            FROM laplace.recall_intent(@shape, @topic, @topic2, @type, @lang, @ctx)
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.AddWithValue("shape", shape);
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.Add("topic2", NpgsqlDbType.Bytea).Value = (object?)topic2 ?? DBNull.Value;
            cmd.Parameters.AddWithValue("type", (object?)relationType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lang", (object?)lang ?? DBNull.Value);
            cmd.Parameters.Add("ctx", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
                (object?)contextIds ?? DBNull.Value;
        }, "recall_intent", ct);
    }

    /// <summary>
    /// Every edge of a topic inside the selected bands, both directions, ranked
    /// by eff_mu. Selecting bands is how a read narrows without naming a single
    /// relation type and without naming a language.
    /// </summary>
    private Task<IReadOnlyList<QueryRow>> BandFactsAsync(
        byte[] topic, int[]? bands, int limit, string? lang, CancellationToken ct)
    {
        const string sql = """
            SELECT b.name || ' · ' || laplace.relation_canonical(z.type_id) || ' → '
                     || laplace.label_or_hex(z.other) AS reply,
                   -- eff_mu() is fixed-point bigint and carries the index;
                   -- eff_mu_display() is the numeric the client reads.
                   laplace.eff_mu_display(z.rating, z.rd) AS eff_mu,
                   z.witness_count
            FROM (
                SELECT c.type_id, c.object_id AS other, c.rating, c.rd, c.witness_count
                FROM laplace.consensus c
                WHERE c.subject_id = @topic
                  AND (@bands::int[] IS NULL
                       OR laplace.relation_highway_band(c.type_id) = ANY(@bands::int[]))
                UNION ALL
                SELECT c.type_id, c.subject_id, c.rating, c.rd, c.witness_count
                FROM laplace.consensus c
                WHERE c.object_id = @topic
                  AND (@bands::int[] IS NULL
                       OR laplace.relation_highway_band(c.type_id) = ANY(@bands::int[]))
            ) z
            -- The naming catalog, not the counting one: this must not drag a
            -- full-consensus aggregate into every band_facts read.
            JOIN laplace.relation_band_catalog() b
              ON b.band = laplace.relation_highway_band(z.type_id)
            ORDER BY b.rank DESC, laplace.eff_mu(z.rating, z.rd) DESC
            LIMIT @limit
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                (object?)bands ?? DBNull.Value;
            cmd.Parameters.AddWithValue("limit", limit);
        }, "band_facts", ct);
    }

    /// <summary>
    /// Beam search over the consensus graph. The band selection becomes the
    /// highway intent mask — the same bit surface walk_branches already gates
    /// on, so narrowing the lens narrows the scan rather than filtering after it.
    /// </summary>
    private Task<IReadOnlyList<QueryRow>> BeamAsync(
        byte[] topic, string? relationType, int[]? bands, QueryDials dials, CancellationToken ct)
    {
        // An unfiltered walk_branches call Append-scans every relation-type
        // partition (~24s, measured — see recall_walk_response). A band lens or
        // a named relation type keeps the scan bounded; with neither, take the
        // greedy single chain instead of the beam.
        var haveLens = !string.IsNullOrWhiteSpace(relationType) || (bands is { Length: > 0 });
        if (!haveLens)
        {
            const string greedy = """
                SELECT laplace.label_or_hex(w.entity_id)
                         || ' (' || laplace.relation_canonical(w.type_id) || ')' AS reply,
                       w.eff_mu, NULL::bigint
                FROM laplace.walk_strongest(@topic, NULL, @depth) w
                ORDER BY w.step
                """;
            return ReadRowsAsync(greedy, ReadQueryRow, cmd =>
            {
                cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
                cmd.Parameters.AddWithValue("depth", dials.Depth);
            }, "walk_strongest", ct);
        }

        const string sql = """
            WITH mask AS (
                SELECT CASE WHEN @bands::int[] IS NULL THEN NULL
                            ELSE laplace.laplace_highway_mask_from_bits(
                                     (SELECT array_agg(DISTINCT bit)
                                      FROM unnest(@bands::int[]) AS b(band),
                                           LATERAL unnest(laplace.laplace_highway_mask_bits(
                                               laplace.laplace_highway_band_mask(b.band))) AS t(bit)))
                       END AS m
            )
            SELECT repeat('  ', w.depth) || laplace.realize_path(w.path, w.types) AS reply,
                   w.path_mu, w.witnesses
            FROM mask, laplace.walk_branches(
                     @topic,
                     CASE WHEN @type::text IS NULL THEN NULL
                          ELSE laplace.relation_type_id(@type::text) END,
                     @depth, @breadth, mask.m) w
            ORDER BY w.path_mu DESC
            LIMIT @limit
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.AddWithValue("type", (object?)relationType ?? DBNull.Value);
            cmd.Parameters.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                (object?)bands ?? DBNull.Value;
            cmd.Parameters.AddWithValue("depth", dials.Depth);
            cmd.Parameters.AddWithValue("breadth", dials.Breadth);
            cmd.Parameters.AddWithValue("limit", dials.Limit);
        }, "walk_branches", ct);
    }

    /// <summary>Admissible geometric A* between two topics; Dijkstra by default.</summary>
    private Task<IReadOnlyList<QueryRow>> PathAsync(
        byte[] topic, byte[]? topic2, QueryDials dials, CancellationToken ct)
    {
        if (topic2 is null)
            return Task.FromResult<IReadOnlyList<QueryRow>>([
                new QueryRow("path needs a second topic.", null, null)]);

        const string sql = """
            SELECT repeat('  ', p.step) || laplace.label_or_hex(p.entity_id) AS reply,
                   p.g::numeric, NULL::bigint
            FROM laplace.astar_path(@topic, ARRAY[@topic2]::bytea[], @depth, NULL,
                                    @directed, @geometry) p
            ORDER BY p.step
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.Add("topic2", NpgsqlDbType.Bytea).Value = topic2;
            cmd.Parameters.AddWithValue("depth", dials.Depth);
            cmd.Parameters.AddWithValue("directed", dials.Directed);
            cmd.Parameters.AddWithValue("geometry", dials.UseGeometry);
        }, "astar_path", ct);
    }

    /// <summary>Nearest content by position on S³ and by trajectory shape.</summary>
    private Task<IReadOnlyList<QueryRow>> GeometricNeighborsAsync(
        byte[] topic, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT n.neighbor || '  (geodesic ' || round(n.geodesic::numeric, 4)
                     || ', frechet ' || round(n.frechet::numeric, 4) || ')' AS reply,
                   NULL::numeric, NULL::bigint
            FROM laplace.structural_neighbors_of(@topic, @k) n
            ORDER BY n.geodesic
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.AddWithValue("k", limit);
        }, "structural_neighbors_of", ct);
    }

    /// <summary>Trajectory descent. Seeded, so a generation is reproducible.</summary>
    private Task<IReadOnlyList<QueryRow>> GenerateAsync(
        byte[] topic, QueryDials dials, CancellationToken ct)
    {
        const string sql = """
            SELECT laplace.label_or_hex(w.entity) AS reply, NULL::numeric, NULL::bigint
            FROM laplace.walk_continuations(ARRAY[@topic]::bytea[], @steps, @stride,
                                            @spread, @breadth, @seed) w
            ORDER BY w.step
            """;
        return ReadRowsAsync(sql, ReadQueryRow, cmd =>
        {
            cmd.Parameters.Add("topic", NpgsqlDbType.Bytea).Value = topic;
            cmd.Parameters.AddWithValue("steps", dials.Steps);
            cmd.Parameters.AddWithValue("stride", dials.MaxStride);
            cmd.Parameters.AddWithValue("spread", dials.Spread);
            cmd.Parameters.AddWithValue("breadth", dials.Breadth);
            cmd.Parameters.AddWithValue("seed", (object?)dials.Seed ?? DBNull.Value);
        }, "walk_continuations", ct);
    }

    private static QueryRow ReadQueryRow(NpgsqlDataReader r) => new(
        r.IsDBNull(0) ? string.Empty : r.GetString(0),
        r.IsDBNull(1) ? null : r.GetDecimal(1),
        r.IsDBNull(2) ? null : r.GetInt64(2));

    private async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        string sql, Func<NpgsqlDataReader, T> map, Action<NpgsqlCommand> bind,
        string label, CancellationToken ct, int timeoutSeconds = 0)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
            bind(cmd);

            var rows = new List<T>(16);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(map(reader));
            return rows;
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"{label} query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }
}

/// <summary>Clamped dials for one read. Bounds live here, not scattered as literals.</summary>
internal readonly record struct QueryDials(
    int Depth, int Breadth, int Limit, int Steps, double Spread, int MaxStride,
    long? Seed, bool Directed, bool UseGeometry)
{
    public static QueryDials From(QueryRequest req) => new(
        Depth: Math.Clamp(req.Depth ?? 4, 1, 16),
        Breadth: Math.Clamp(req.Breadth ?? 5, 1, 32),
        Limit: Math.Clamp(req.Limit ?? 40, 1, 500),
        Steps: Math.Clamp(req.Steps ?? 24, 1, 256),
        Spread: Math.Clamp(req.Spread ?? 0.7, 0.0, 1.0),
        MaxStride: Math.Clamp(req.MaxStride ?? 5, 1, 8),
        Seed: req.Seed,
        Directed: req.Directed ?? false,
        UseGeometry: req.UseGeometry ?? false);
}
