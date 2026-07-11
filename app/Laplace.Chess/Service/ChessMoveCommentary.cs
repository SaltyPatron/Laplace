using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Substrate-backed move commentary: eval line + motif + book EXPLAINS + recall/salient_facts
/// templates (no LLM).
/// </summary>
public static class ChessMoveCommentary
{
    public const int LichessMaxChars = 140;

    public sealed record Inputs(
        int ScoreCp,
        int Depth,
        IReadOnlyList<string> Pv,
        IReadOnlyList<string> Motifs,
        string? PositionSurface = null);

    public static async Task<string> BuildAsync(
        NpgsqlDataSource ds, Inputs input, CancellationToken ct = default, int maxChars = LichessMaxChars)
    {
        var parts = new List<string>(4);
        string evalLine = FormatEval(input.ScoreCp, input.Depth);
        if (evalLine.Length > 0) parts.Add(evalLine);

        string? motifLine = null;
        foreach (var motif in input.Motifs)
        {
            motifLine = await MotifLineAsync(ds, motif, ct);
            if (motifLine is not null) break;
        }
        if (motifLine is not null) parts.Add(motifLine);

        // The chess literature's judgment of this exact position, if a book attested one —
        // (text, EXPLAINS, position) edges deposited by ChessBookDecomposer.
        if (input.PositionSurface is { } surface
            && await BookLineAsync(ds, surface, ct) is { } bookLine)
            parts.Add(bookLine);

        if (input.Pv.Count > 0)
            parts.Add($"PV {string.Join(' ', input.Pv.Take(3))}");

        return Truncate(string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), maxChars);
    }

    private static readonly Hash128 ExplainsRelation = RelationTypeRegistry.RelationTypeId("EXPLAINS");

    private static async Task<string?> BookLineAsync(
        NpgsqlDataSource ds, string positionSurface, CancellationToken ct)
    {
        try
        {
            Hash128 posId;
            lock (ChessCompose.Gate) { posId = ChessCompose.PositionId(positionSurface); }

            Hash128 textId;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT subject_id FROM laplace.attestations "
                    + "WHERE object_id = $1 AND type_id = $2 LIMIT 1";
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = posId.ToBytes() });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = ExplainsRelation.ToBytes() });
                var scalar = await cmd.ExecuteScalarAsync(ct);
                if (scalar is not byte[] bytes) return null;
                textId = Hash128.FromBytes(bytes);
            }

            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var render = conn.CreateCommand())
            {
                render.CommandText = "SELECT laplace.render_text_batch($1, 48)";
                render.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    Value = new[] { textId.ToBytes() },
                });
                var rendered = await render.ExecuteScalarAsync(ct);
                if (rendered is string[] { Length: > 0 } texts && !string.IsNullOrWhiteSpace(texts[0]))
                    return $"Book: {Truncate(texts[0], 70)}";
            }
        }
        catch
        {
            // Commentary is decoration; a failed lookup must never affect play.
        }
        return null;
    }

    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        if (text.Length <= maxChars) return text;
        if (maxChars <= 1) return text[..maxChars];
        return text[..(maxChars - 1)] + "…";
    }

    private static string FormatEval(int scoreCp, int depth)
    {
        if (depth <= 0) return "";
        if (Math.Abs(scoreCp) >= 29_000)
            return scoreCp > 0 ? "Mating" : "Getting mated";
        double pawns = scoreCp / 100.0;
        string sign = pawns >= 0 ? "+" : "";
        return $"Eval {sign}{pawns:0.0} (d{depth})";
    }

    private static async Task<string?> MotifLineAsync(NpgsqlDataSource ds, string motif, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        await using (var recall = conn.CreateCommand())
        {
            recall.CommandText = "SELECT reply, eff_mu FROM laplace.recall(@p) ORDER BY eff_mu DESC LIMIT 1";
            recall.Parameters.AddWithValue("p", $"define {motif}");
            await using var r = await recall.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                string reply = r.IsDBNull(0) ? "" : r.GetString(0);
                if (!string.IsNullOrWhiteSpace(reply))
                    return $"{Label(motif)}: {Truncate(reply, 60)}";
            }
        }

        await using (var traj = conn.CreateCommand())
        {
            traj.CommandText =
                "SELECT answer FROM laplace.recall_trajectories(laplace.word_id(@w), @k) LIMIT 1";
            traj.Parameters.AddWithValue("w", motif);
            traj.Parameters.AddWithValue("k", 2);
            var scalar = await traj.ExecuteScalarAsync(ct);
            if (scalar is string answer && !string.IsNullOrWhiteSpace(answer))
                return $"{Label(motif)} — {Truncate(answer, 60)}";
        }

        await using (var facts = conn.CreateCommand())
        {
            facts.CommandText =
                "SELECT fact FROM laplace.salient_facts(laplace.word_id(@w), NULL, @k) "
                + "ORDER BY eff_mu DESC LIMIT 1";
            facts.Parameters.AddWithValue("w", motif);
            facts.Parameters.AddWithValue("k", 3);
            var scalar = await facts.ExecuteScalarAsync(ct);
            if (scalar is string fact && !string.IsNullOrWhiteSpace(fact))
                return $"{Label(motif)} — {Truncate(fact, 60)}";
        }

        return inputMotifOnly(motif);
    }

    private static string? inputMotifOnly(string motif) => motif switch
    {
        "fork" => "Fork — two targets at once",
        "discovered_check" => "Discovered check",
        "hanging_piece_won" => "Won material",
        _ => Label(motif),
    };

    private static string Label(string motif) => motif switch
    {
        "fork" => "Fork",
        "discovered_check" => "Discovered check",
        "hanging_piece_won" => "Material win",
        _ => motif.Replace('_', ' '),
    };
}
