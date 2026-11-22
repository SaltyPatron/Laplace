using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Standing of each [position, move, position] transition claim read via <c>public.laplace_standings</c>.
/// </summary>
public static class NpgsqlChessTransitionStandings
{
    public readonly record struct Row(double Confidence, double Deviation, long Matches);

    public static Dictionary<Hash128, Row> Read(NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> claims)
    {
        var map = new Dictionary<Hash128, Row>(claims.Count);
        if (claims.Count == 0) return map;
        using var conn = dataSource.OpenConnection();
        using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("chess.transition_standings"),
            parameters => AddIds(parameters, claims));
        cmd.Prepare();
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
        await cmd.PrepareAsync(ct).ConfigureAwait(false);
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
