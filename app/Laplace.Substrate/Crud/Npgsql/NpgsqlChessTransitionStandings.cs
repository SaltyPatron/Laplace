using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// <c>chess.transition_standings</c>: the Native standing of each [position, move, position] claim a
/// recorded game attests (Laplace-Engine <c>src/chess.c</c>; ids from <c>ChessEngineIdentity</c>),
/// read through <c>public.laplace_standings</c>. A claim no game attested has no row.
/// </summary>
public static class NpgsqlChessTransitionStandings
{
    /// <summary>
    /// A claim's conservative rating (Native <c>laplace_confidence</c>, k = 2), its deviation and the
    /// games folded into it, on the Glicko-2 scale of <c>LP_GLICKO_RATING</c>.
    /// </summary>
    public readonly record struct Row(double Confidence, double Deviation, long Matches);

    public static Dictionary<Hash128, Row> Read(NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> claims)
    {
        var map = new Dictionary<Hash128, Row>(claims.Count);
        if (claims.Count == 0) return map;
        using var conn = dataSource.OpenConnection();
        using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("chess.transition_standings"),
            parameters => AddIds(parameters, claims));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[Hash128.FromBytes((byte[])reader[0])] = new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3));
        return map;
    }

    public static async Task<Dictionary<Hash128, Row>> ReadAsync(
        NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> claims, CancellationToken ct = default)
    {
        var map = new Dictionary<Hash128, Row>(claims.Count);
        if (claims.Count == 0) return map;
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("chess.transition_standings"),
            parameters => AddIds(parameters, claims));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map[Hash128.FromBytes((byte[])reader[0])] = new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3));
        return map;
    }

    private static void AddIds(NpgsqlParameterCollection parameters, IReadOnlyCollection<Hash128> claims)
    {
        var raw = new byte[claims.Count][];
        int i = 0;
        foreach (var id in claims) raw[i++] = id.ToBytes();
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = raw });
    }
}
