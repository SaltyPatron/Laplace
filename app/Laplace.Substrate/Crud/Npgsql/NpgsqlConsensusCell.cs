using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One consensus cell by (subject, type, object), returning raw Glicko-2
/// <c>rating</c>/<c>rd</c>/<c>witness_count</c> rather than the effective rating that
/// <see cref="NpgsqlConsensusByIds"/> returns. The installed <c>consensus.cell</c> looks the
/// row up by its primary id (<c>laplace.consensus_id</c>).
/// </summary>
public static class NpgsqlConsensusCell
{
    public readonly record struct Row(long Rating, long Rd, long WitnessCount);

    private const string Sql =
        "SELECT rating, rd, witness_count FROM consensus.cell($1, $2, $3)";

    public static async Task<Row?> ReadAsync(
        NpgsqlDataSource dataSource, Hash128 subject, Hash128 typeId, Hash128 obj,
        CancellationToken ct = default)
    {
        // Orient the triple with the native witness builder first: a symmetric
        // relation may store its endpoints opposite to the caller's order, and the
        // un-oriented triple would miss the cell. Nothing is staged or persisted.
        var oriented = Laplace.Decomposers.Abstractions.NativeAttestation.CategoricalResolved(
            subject, typeId, obj, Hash128.Zero, null, 1);
        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource, Sql,
            static r => new Row(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = oriented.SubjectId.ToBytes() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = typeId.ToBytes() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = oriented.ObjectId!.Value.ToBytes() });
            },
            ct: ct,
            label: "consensus_cell").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}
