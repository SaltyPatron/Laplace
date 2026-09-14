using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct NamedBandLeaderRow(
        int Band, string BandName, string SubjectIdHex, string Subject, string Relation,
        string ObjectIdHex, string Object, decimal EffMu, long Witnesses);

    /// <summary>
    /// Bounded leaders with immutable band naming in the same set-wise command.
    /// Band names come from <c>converse.relation_band_catalog()</c>; the live
    /// <c>converse.relation_bands()</c> census is deliberately not on this request path.
    /// </summary>
    public static Task<IReadOnlyList<NamedBandLeaderRow>> BandLeadersNamedAsync(
        NpgsqlDataSource dataSource, int[] bands, int perBand, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("leaders.named"),
            static r => new NamedBandLeaderRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6), r.GetDecimal(7), r.GetInt64(8)),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.Add("per", NpgsqlDbType.Integer).Value = perBand;
            }, ct: ct, onError: onError);
}
