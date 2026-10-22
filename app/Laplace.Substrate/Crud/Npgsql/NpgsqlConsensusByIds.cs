using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// <c>consensus.by_ids($1, $2)</c>: folded standing for a set of edge ids under one
/// relation type, the type being the partition key that prunes the read. Synchronous and
/// asynchronous forms bind the same statement.
/// </summary>
public static class NpgsqlConsensusByIds
{
    /// <summary>
    /// One consensus cell's effective rating, deviation and witness count.
    /// </summary>
    public readonly record struct Row(double EffMu, double Rd, double Witnesses);

    /// <summary>Synchronous read for callers inside non-async search.</summary>
    public static Dictionary<Hash128, Row> Read(
        NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> edgeIds, Hash128 relationType)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("consensus.by_ids"), parameters =>
        {
            AddIds(parameters, edgeIds);
            AddType(parameters, relationType);
        });
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<Hash128, Row>(edgeIds.Count);
        while (reader.Read())
            map[Hash128.FromBytes((byte[])reader[0])] =
                new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        return map;
    }

    /// <summary>
    /// Reads two relation batches, each pruned to its own partition, in one command and one
    /// round trip; the leading discriminator column routes each row to its batch.
    /// </summary>
    public static (Dictionary<Hash128, Row> First, Dictionary<Hash128, Row> Second) ReadPair(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<Hash128> firstIds, Hash128 firstRelationType,
        IReadOnlyCollection<Hash128> secondIds, Hash128 secondRelationType)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("consensus.by_ids_pair"), parameters =>
        {
            AddIds(parameters, firstIds);
            AddType(parameters, firstRelationType);
            AddIds(parameters, secondIds);
            AddType(parameters, secondRelationType);
        });
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

    /// <summary>
    /// Reads three relation batches, each pruned to its own partition, in one command.
    /// The leading discriminator is 0, 1, or 2.
    /// </summary>
    public static (
        Dictionary<Hash128, Row> First,
        Dictionary<Hash128, Row> Second,
        Dictionary<Hash128, Row> Third) ReadTriple(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<Hash128> firstIds, Hash128 firstRelationType,
        IReadOnlyCollection<Hash128> secondIds, Hash128 secondRelationType,
        IReadOnlyCollection<Hash128> thirdIds, Hash128 thirdRelationType)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("consensus.by_ids_triple"), parameters =>
        {
            AddIds(parameters, firstIds);
            AddType(parameters, firstRelationType);
            AddIds(parameters, secondIds);
            AddType(parameters, secondRelationType);
            AddIds(parameters, thirdIds);
            AddType(parameters, thirdRelationType);
        });
        using var reader = cmd.ExecuteReader();
        var first = new Dictionary<Hash128, Row>(firstIds.Count);
        var second = new Dictionary<Hash128, Row>(secondIds.Count);
        var third = new Dictionary<Hash128, Row>(thirdIds.Count);
        while (reader.Read())
        {
            var target = reader.GetInt32(0) switch
            {
                0 => first,
                1 => second,
                _ => third,
            };
            target[Hash128.FromBytes((byte[])reader[1])] =
                new Row(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
        }
        return (first, second, third);
    }

    public static async Task<Dictionary<Hash128, Row>> ReadAsync(
        NpgsqlDataSource dataSource, IReadOnlyCollection<Hash128> edgeIds, Hash128 relationType,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get("consensus.by_ids"), parameters =>
        {
            AddIds(parameters, edgeIds);
            AddType(parameters, relationType);
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<Hash128, Row>(edgeIds.Count);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map[Hash128.FromBytes((byte[])reader[0])] =
                new Row(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        return map;
    }

    private static void AddIds(NpgsqlParameterCollection parameters, IReadOnlyCollection<Hash128> edgeIds)
    {
        var raw = new byte[edgeIds.Count][];
        var i = 0;
        foreach (var id in edgeIds) raw[i++] = id.ToBytes();
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            Value = raw,
        });
    }

    private static void AddType(NpgsqlParameterCollection parameters, Hash128 relationType)
    {
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bytea,
            Value = relationType.ToBytes(),
        });
    }
}
