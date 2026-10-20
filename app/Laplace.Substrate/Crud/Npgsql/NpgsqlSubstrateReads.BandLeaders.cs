using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct NamedBandLeaderRow(
        int Band, string BandName, BandLeaderRow? Leader);

    /// <summary>
    /// Top consensus cells per relation band, with band names, in one set command.
    /// Band names come from the fixed <c>converse.relation_band_catalog()</c>, not the
    /// live <c>converse.relation_bands()</c> census. An empty requested band has no
    /// leader, preserving the distinction between an empty arena and an omitted read.
    /// </summary>
    public static Task<IReadOnlyList<NamedBandLeaderRow>> BandLeadersNamedAsync(
        NpgsqlDataSource dataSource, int[] bands, int perBand, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("leaders.named"),
            static r => new NamedBandLeaderRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : new BandLeaderRow(
                    r.GetInt32(0), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetInt64(8))),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.Add("per", NpgsqlDbType.Integer).Value = perBand;
            }, ct: ct, onError: onError);
}
