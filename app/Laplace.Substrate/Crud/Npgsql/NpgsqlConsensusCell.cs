using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One consensus cell by (subject, type, object) — the feedback/bookkeeping shape that
/// needs raw Glicko <c>rating</c>/<c>rd</c>/<c>witness_count</c>, not the
/// <see cref="NpgsqlConsensusByIds"/> display axis (<c>eff_mu</c>).
///
/// Routes through the installed <c>laplace.consensus_cell</c> (GH #909), which looks the
/// row up by primary id (<c>laplace.consensus_id</c>) rather than scanning the triple.
/// This type is now a typed wrapper over that surface, not a hand-written table read.
/// </summary>
public static class NpgsqlConsensusCell
{
    public readonly record struct Row(long Rating, long Rd, long WitnessCount);

    private const string Sql =
        "SELECT rating, rd, witness_count FROM laplace.consensus_cell($1, $2, $3)";

    public static async Task<Row?> ReadAsync(
        NpgsqlDataSource dataSource, Hash128 subject, Hash128 typeId, Hash128 obj,
        CancellationToken ct = default)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource, Sql,
            static r => new Row(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = subject.ToBytes() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = typeId.ToBytes() });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = obj.ToBytes() });
            },
            ct: ct,
            label: "consensus_cell").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}
