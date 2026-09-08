using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One consensus cell by (subject, type, object) — the feedback/bookkeeping shape that
/// needs raw Glicko <c>rating</c>/<c>rd</c>/<c>witness_count</c>, not the
/// <see cref="NpgsqlConsensusByIds"/> display axis (<c>eff_mu</c>).
///
/// Routes through the installed <c>consensus.cell</c> (GH #909), which looks the
/// row up by primary id (<c>laplace.consensus_id</c>) rather than scanning the triple.
/// This type is now a typed wrapper over that surface, not a hand-written table read.
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
        // The witness builder owns relation orientation. In particular, a
        // symmetric relation may store the endpoints opposite to the caller's
        // order; probing the un-oriented triple falsely reported no evidence.
        // This native calculation does not stage or persist an observation.
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
