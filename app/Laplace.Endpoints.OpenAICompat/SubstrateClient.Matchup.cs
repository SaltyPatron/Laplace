using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The league surface: per-band leaderboards, entity verdict records, and the
/// head-to-head matchup. The rating math is a literal sports rating — Glicko-2 —
/// so this presents it as one: leaders per arena, games played, win/loss record.
/// Split into fast reads (leaders, record, tape) and the slow path/verdict read,
/// which the UI fetches lazily; path search competes with active seeds for the
/// box, so it must never block the parts that return in a second.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>Top consensus edges per salience band, fully labeled.</summary>
    public async Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct)
    {
        const string sql = """
            SELECT b.band,
                   encode(e.subject_id, 'hex'),
                   laplace.label_or_hex(e.subject_id),
                   laplace.relation_canonical(e.type_id),
                   encode(e.object_id, 'hex'),
                   laplace.label_or_hex(e.object_id),
                   laplace.eff_mu_display(e.rating, e.rd),
                   e.witness_count
            FROM unnest(@bands) AS b(band)
            CROSS JOIN LATERAL laplace.consensus_band_edges(b.band, NULL, @per) e
            """;
        var rows = await ReadRowsAsync(sql,
            static r => (Band: r.GetInt32(0), Row: new LeaderRow(
                r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5),
                r.GetDecimal(6), r.GetInt64(7))),
            cmd =>
            {
                cmd.Parameters.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                cmd.Parameters.AddWithValue("per", perBand);
            }, "band_leaders", ct);

        var catalog = await RelationBandsAsync(ct);
        var names = catalog.ToDictionary(b => b.Band, b => b.Name);
        return rows.GroupBy(r => r.Band)
            .OrderBy(g => g.Key)
            .Select(g => new BandLeaders(g.Key, names.GetValueOrDefault(g.Key, $"band {g.Key}"),
                [.. g.Select(r => r.Row)]))
            .ToList();
    }

    /// <summary>
    /// The entity's record: its edges scored by the canonical verdict logic.
    /// epistemic_status IS that logic — the counts are grouped server-side and
    /// never re-derived from raw μ in a client.
    /// </summary>
    public async Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct)
    {
        if (!TryParseHex(idHex, out var id)) return null;
        const string sql = """
            SELECT s.status, count(*)
            FROM laplace.epistemic_status(@id, NULL, 500) s
            GROUP BY s.status
            """;
        var rows = await ReadRowsAsync(sql,
            static r => (Status: r.GetString(0), Count: r.GetInt64(1)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "entity_record", ct);
        long Of(string s) => rows.FirstOrDefault(r => r.Status == s).Count;
        return new EntityRecordResponse("entity.record", idHex.ToLowerInvariant(),
            Of("confirmed"), Of("contested"), Of("refuted"), Of("thin"));
    }

    /// <summary>The fast half of a matchup: both cards plus the tale of the tape.</summary>
    public async Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct)
    {
        var x = await ResolveTopicAsync(xRef, ct);
        var y = await ResolveTopicAsync(yRef, ct);
        if (x is null || y is null) return null;

        var xHex = Convert.ToHexString(x.Value.Id).ToLowerInvariant();
        var yHex = Convert.ToHexString(y.Value.Id).ToLowerInvariant();

        // The three cheap reads are independent — run them together.
        var tapeTask = TapeAsync(x.Value.Id, y.Value.Id, ct);
        var xSideTask = SideAsync(xHex, x.Value.Id, x.Value.Label, ct);
        var ySideTask = SideAsync(yHex, y.Value.Id, y.Value.Label, ct);
        await Task.WhenAll(tapeTask, xSideTask, ySideTask);

        return new MatchupResponse("matchup", xSideTask.Result, ySideTask.Result, tapeTask.Result);
    }

    private async Task<MatchupSide> SideAsync(string hex, byte[] id, string label, CancellationToken ct)
    {
        var recordTask = EntityRecordAsync(hex, ct);
        var factsTask = ReadRowsAsync("""
            SELECT f.type, f.fact, f.eff_mu, f.witnesses
            FROM laplace.salient_facts(@id, NULL, 6) f
            """,
            static r => new SalientFactRow(r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "salient_facts", ct);
        await Task.WhenAll(recordTask, factsTask);
        return new MatchupSide(hex, label,
            recordTask.Result ?? new EntityRecordResponse("entity.record", hex, 0, 0, 0, 0),
            factsTask.Result);
    }

    private Task<IReadOnlyList<TapeRow>> TapeAsync(byte[] x, byte[] y, CancellationToken ct) =>
        ReadRowsAsync("""
            SELECT c.holder, c.type, c.fact, c.mu
            FROM laplace.contrast(@x, @y, NULL, 60) c
            """,
            static r => new TapeRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3)),
            cmd =>
            {
                cmd.Parameters.Add("x", NpgsqlDbType.Bytea).Value = x;
                cmd.Parameters.Add("y", NpgsqlDbType.Bytea).Value = y;
            }, "contrast", ct);

    /// <summary>
    /// The slow half: relation_summary's path search and verdict. Measured
    /// 6–14s under an active seed — served separately so the tape never waits.
    /// </summary>
    public async Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct)
    {
        var x = await ResolveTopicAsync(xRef, ct);
        var y = await ResolveTopicAsync(yRef, ct);
        if (x is null || y is null) return null;

        const string sql = """
            SELECT s.relation, s.plane, s.mu, s.usage, s.geodesic, s.verdict
            FROM laplace.relation_summary(@x, @y) s
            """;
        var rows = await ReadRowsAsync(sql,
            static r => new MatchupVerdictResponse("matchup.verdict",
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetString(5)),
            cmd =>
            {
                cmd.Parameters.Add("x", NpgsqlDbType.Bytea).Value = x.Value.Id;
                cmd.Parameters.Add("y", NpgsqlDbType.Bytea).Value = y.Value.Id;
            }, "relation_summary", ct, timeoutSeconds: 120);
        return rows.Count == 0 ? new MatchupVerdictResponse("matchup.verdict", null, null, null, null, null, null) : rows[0];
    }

    private static bool TryParseHex(string idHex, out byte[] id)
    {
        id = [];
        if (idHex.Length != 32) return false;
        try { id = Convert.FromHexString(idHex); return true; }
        catch (FormatException) { return false; }
    }
}
