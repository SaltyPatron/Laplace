using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One consensus cell by (subject, type, object) — the feedback/bookkeeping shape that
/// needs raw Glicko <c>rating</c>/<c>rd</c>/<c>witness_count</c>, not the
/// <see cref="NpgsqlConsensusByIds"/> display axis (<c>eff_mu</c>).
///
/// Looked up by primary id (<c>laplace.consensus_id</c>), not a triple scan. There is no
/// installed <c>consensus_cell</c>/<c>edge_strength</c> yet (doc 41); until that lands this
/// is the one sanctioned reader for the shape. Callers must not hand-write the table.
/// </summary>
public static class NpgsqlConsensusCell
{
    public readonly record struct Row(long Rating, long Rd, long WitnessCount);

    private const string Sql =
        "SELECT rating, rd, witness_count FROM laplace.consensus "
        + "WHERE id = laplace.consensus_id($1, $2, $3)";

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
