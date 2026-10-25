using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct NamedClaimLeaderRow(int Band, string BandName,
        string? ClaimId, string? Parts, double? Confidence, long? Witnesses, string? Trajectory);

    public static Task<IReadOnlyList<NamedClaimLeaderRow>> ClaimLeadersNamedAsync(
        NpgsqlDataSource dataSource, int[] bands, int perBand, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("leaders.native_named"),
            static r => new NamedClaimLeaderRow(r.GetInt32(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetDouble(4), r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetString(6)),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.Add("per", NpgsqlDbType.Integer).Value = perBand;
            }, ct: ct, onError: onError);

}
