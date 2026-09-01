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

    private const string PairSql = """
        SELECT 0, id, eff_mu, rd, witness_count FROM consensus.by_ids($1, $2)
        UNION ALL
        SELECT 1, id, eff_mu, rd, witness_count FROM consensus.by_ids($3, $4)
        """;

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

    /// <summary>
    /// Read two independently partition-pruned relation batches in one database command.
    /// Chess root evaluation needs exact state transitions and typed move outcomes together;
    /// opening two connections and paying two client/server turns for one decision is needless.
    /// </summary>
    public static (Dictionary<Hash128, Row> First, Dictionary<Hash128, Row> Second) ReadPair(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<Hash128> firstIds, Hash128 firstRelationType,
        IReadOnlyCollection<Hash128> secondIds, Hash128 secondRelationType)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = PairSql;
        AddIds(cmd, firstIds);
        AddType(cmd, firstRelationType);
        AddIds(cmd, secondIds);
        AddType(cmd, secondRelationType);
        using var reader = cmd.ExecuteReader();
        var first = new Dictionary<Hash128, Row>(firstIds.Count);
        var second = new Dictionary<Hash128, Row>(secondIds.Count);
        while (reader.Read())
        {
            var target = reader.GetInt32(0) == 0 ? first : second;
            target[Hash128.FromBytes((byte[])reader[1])] =
                new Row(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
        }
        return (first, second);
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
        cmd.CommandText = Sql;
        AddIds(cmd, edgeIds);
        AddType(cmd, relationType);
    }

    private static void AddIds(NpgsqlCommand cmd, IReadOnlyCollection<Hash128> edgeIds)
    {
        var raw = new byte[edgeIds.Count][];
        var i = 0;
        foreach (var id in edgeIds) raw[i++] = id.ToBytes();
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            Value = raw,
        });
    }

    private static void AddType(NpgsqlCommand cmd, Hash128 relationType)
    {
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bytea,
            Value = relationType.ToBytes(),
        });
    }
}
