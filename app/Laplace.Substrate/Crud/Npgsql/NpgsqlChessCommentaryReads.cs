using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Occurrence read for one position entity: which game-line trajectories contain it and
/// the recorded playings behind those lines. Precedents are ordered by the mover's
/// Glicko-2 lower bound, then source Elo, then an opening or motif match. The GIN
/// probe finds the lines; it does not decide their order.
/// </summary>
public static class NpgsqlChessCommentaryReads
{
    public readonly record struct PositionHistoryRow(
        byte[] LineId,
        byte[] PlayingId,
        string? PlayedOn,
        byte[]? WhiteId,
        string White,
        byte[]? BlackId,
        string Black,
        string? Result,
        double? MoverEffMu,
        int? MoverElo,
        int OpeningOrMotif,
        long WitnessCount);

    public readonly record struct PositionContinuationCount(byte[] NextPositionId, long Count);

    /// <summary>
    /// Recorded playings whose line contains <paramref name="positionId"/>.
    /// <paramref name="witnessCount"/> on every row is the number of such playings.
    /// <paramref name="containerLimit"/> remains so callers keep a positive bound; it
    /// is not a cut of the GIN page.
    /// </summary>
    public static Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        short projectionType,
        int containerLimit,
        int limit,
        CancellationToken ct,
        bool whiteToMove = true,
        string? eco = null,
        string? motif = null,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH raw_lines AS MATERIALIZED (
                SELECT DISTINCT p.entity_id AS line_id
                FROM laplace.physicalities p
                JOIN laplace.entities e ON e.id = p.entity_id
                WHERE p.type = @projection_type
                  AND e.type_id = @game_type
                  AND p.trajectory IS NOT NULL
                  AND public.laplace_trajectory_constituent_ids(p.trajectory)
                      @> ARRAY[@position]::bytea[]
                  AND @container_limit >= 1
            ),
            playings AS MATERIALIZED (
                SELECT l.line_id, h.event_id, h.played_on,
                       g.white_id, g.white, g.black_id, g.black, g.result, g.eco,
                       CASE WHEN @white_to_move THEN g.white_id ELSE g.black_id END AS mover_id
                FROM raw_lines l
                CROSS JOIN LATERAL chess.line(l.line_id) h
                CROSS JOIN LATERAL chess.game(h.event_id) g
            ),
            scored AS MATERIALIZED (
                SELECT p.line_id, p.event_id, p.played_on,
                       p.white_id, p.white, p.black_id, p.black, p.result,
                       consensus.eff_mu(c.rating, c.rd)::double precision AS mover_eff_mu,
                       elo.rating AS mover_elo,
                       (CASE WHEN COALESCE(@eco, '') <> '' AND p.eco = @eco THEN 1 ELSE 0 END
                        + CASE WHEN COALESCE(@motif, '') <> '' AND EXISTS (
                               SELECT 1
                               FROM laplace.attestations a
                               WHERE a.subject_id = p.line_id
                                 AND a.type_id = laplace.relation_type_id('GAME_HAS_MOTIF')
                                 AND realize.realize(a.object_id) = @motif
                           ) THEN 1 ELSE 0 END) AS opening_or_motif
                FROM playings p
                LEFT JOIN LATERAL (
                    SELECT c.rating, c.rd
                    FROM laplace.consensus c
                    WHERE c.subject_id = p.mover_id
                      AND c.type_id = laplace.relation_type_id('OUTCOME')
                      AND consensus.eff_mu(c.rating, c.rd) > 0
                    ORDER BY consensus.eff_mu(c.rating, c.rd) DESC, c.object_id
                    LIMIT 1
                ) c ON p.mover_id IS NOT NULL
                LEFT JOIN LATERAL (
                    SELECT pr.rating
                    FROM chess.player_ratings(p.mover_id) pr
                    LIMIT 1
                ) elo ON p.mover_id IS NOT NULL
            )
            SELECT s.line_id, s.event_id, s.played_on,
                   s.white_id, s.white, s.black_id, s.black, s.result,
                   s.mover_eff_mu, s.mover_elo, s.opening_or_motif,
                   count(*) OVER () AS witness_count
            FROM scored s
            ORDER BY s.mover_eff_mu DESC NULLS LAST,
                     s.mover_elo DESC NULLS LAST,
                     s.opening_or_motif DESC,
                     s.event_id
            LIMIT @limit
            """,
            static r => new PositionHistoryRow(
                r.GetFieldValue<byte[]>(0),
                r.GetFieldValue<byte[]>(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.GetInt32(10),
                r.GetInt64(11)),
            p =>
            {
                p.Add("position", NpgsqlDbType.Bytea).Value = positionId;
                p.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
                p.AddWithValue("projection_type", projectionType);
                p.AddWithValue("container_limit", Math.Clamp(containerLimit, 1, 256));
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
                p.AddWithValue("white_to_move", whiteToMove);
                p.Add("eco", NpgsqlDbType.Text).Value = (object?)eco ?? DBNull.Value;
                p.Add("motif", NpgsqlDbType.Text).Value = (object?)motif ?? DBNull.Value;
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_history",
            onError: onError);

    /// <summary>
    /// How the games that reached <paramref name="positionId"/> left it. The counts are
    /// the next trajectory constituent, not the order of the GIN page.
    /// </summary>
    public static Task<IReadOnlyList<PositionContinuationCount>> PositionContinuationsAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        short projectionType,
        int limit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH raw_lines AS MATERIALIZED (
                SELECT DISTINCT p.entity_id AS line_id
                FROM laplace.physicalities p
                JOIN laplace.entities e ON e.id = p.entity_id
                WHERE p.type = @projection_type
                  AND e.type_id = @game_type
                  AND p.trajectory IS NOT NULL
                  AND public.laplace_trajectory_constituent_ids(p.trajectory)
                      @> ARRAY[@position]::bytea[]
            ),
            steps AS (
                SELECT step.nxt
                FROM raw_lines l
                JOIN laplace.physicalities p
                  ON p.entity_id = l.line_id
                 AND p.type = @projection_type
                CROSS JOIN LATERAL (
                    SELECT ids[i + 1] AS nxt
                    FROM (
                        SELECT public.laplace_trajectory_constituent_ids(p.trajectory) AS ids
                    ) t
                    CROSS JOIN LATERAL generate_subscripts(t.ids, 1) AS s(i)
                    WHERE t.ids[s.i] = @position
                      AND s.i < cardinality(t.ids)
                    LIMIT 1
                ) step
                WHERE step.nxt IS NOT NULL
            )
            SELECT nxt, count(*)::bigint AS n
            FROM steps
            GROUP BY nxt
            ORDER BY n DESC, nxt
            LIMIT @limit
            """,
            static r => new PositionContinuationCount(
                r.GetFieldValue<byte[]>(0),
                r.GetInt64(1)),
            p =>
            {
                p.Add("position", NpgsqlDbType.Bytea).Value = positionId;
                p.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
                p.AddWithValue("projection_type", projectionType);
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_continuations",
            onError: onError);
}
