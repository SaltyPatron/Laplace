using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct RecordingScopeRow(short Kind, byte[] Id, long ObservationCount);

    /// <summary>Actual committed rows for an explicit bounded measurement scope;
    /// this bypasses process presence caches and keeps witness type partition pruning.</summary>
    public static Task<IReadOnlyList<RecordingScopeRow>> RecordingScopeAsync(
        NpgsqlDataSource ds, byte[][] entities, byte[][] physicalities,
        byte[][] witnesses, byte[][] witnessTypes, CancellationToken ct) =>
        NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("ingest.selected_chess_scope"),
            static r => new RecordingScopeRow(r.GetInt16(0), (byte[])r[1], r.GetInt64(2)),
            p =>
            {
                p.Add("entities", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = entities;
                p.Add("physicalities", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = physicalities;
                p.Add("witnesses", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = witnesses;
                p.Add("witness_types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = witnessTypes;
            }, ct: ct, label: "recording_scope_snapshot");
}
