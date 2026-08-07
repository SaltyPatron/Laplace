using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// <c>consensus.by_ids($1, $2)</c> — a batch consensus lookup keyed by a
/// caller-built edge-id array plus one relation type (the partition prune). Four
/// Chess call sites (<c>LearnedPst</c>, <c>SubstrateRootBias</c>,
/// <c>SubstrateStateValuer</c>, <c>SubstrateTurnHost</c>) each hand-wrote this exact
/// open-connection/create-command/bind-two-params/read/build-dictionary block —
/// identical but for which columns they happened to project and whether they awaited.
/// One implementation, both sync (engine search calls this off the hot path but
/// still synchronously) and async (the two call sites already on an async chain).
/// </summary>
public static class NpgsqlConsensusByIds
{
    /// <summary>
    /// A row of <c>consensus_by_ids</c>. All four callers want <see cref="EffMu"/> and
    /// <see cref="Witnesses"/>; only one also wants <see cref="Rd"/> — cheap enough to
    /// always project rather than keep two SQL strings in sync.
    /// </summary>
    public readonly record struct Row(double EffMu, double Rd, double Witnesses);

    private const string Sql =
        "SELECT id, eff_mu, rd, witness_count FROM consensus.by_ids($1, $2)";

    /// <summary>Synchronous — for the engine search path, which is not async.</summary>
    public static Dictionary<Hash128, Row> Read(
        NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> edgeIds, Hash128 relationType)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        Bind(cmd, edgeIds, relationType);
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<Hash128, Row>(edgeIds.Count);
        while (reader.Read())
            map[Hash128.FromBytes((byte[])reader[0])] =
                new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        return map;
    }

    public static async Task<Dictionary<Hash128, Row>> ReadAsync(
        NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> edgeIds, Hash128 relationType,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        Bind(cmd, edgeIds, relationType);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<Hash128, Row>(edgeIds.Count);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map[Hash128.FromBytes((byte[])reader[0])] =
                new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        return map;
    }

    private static void Bind(NpgsqlCommand cmd, IReadOnlyCollection<Hash128> edgeIds, Hash128 relationType)
    {
        var raw = new byte[edgeIds.Count][];
        var i = 0;
        foreach (var id in edgeIds) raw[i++] = id.ToBytes();

        cmd.CommandText = Sql;
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            Value = raw,
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bytea,
            Value = relationType.ToBytes(),
        });
    }
}
