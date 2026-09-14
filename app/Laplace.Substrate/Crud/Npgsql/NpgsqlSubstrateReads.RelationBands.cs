namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct RelationBandCatalogRow(int Band, string Name, double Rank);

    /// <summary>
    /// <c>converse.relation_band_catalog()</c> — immutable salience-band naming only.
    /// Unlike <c>converse.relation_bands()</c>, this performs no live consensus aggregate
    /// and is therefore the correct dependency when a caller needs only band metadata.
    /// </summary>
    public static Task<IReadOnlyList<RelationBandCatalogRow>> RelationBandCatalogAsync(
        global::Npgsql.NpgsqlDataSource dataSource,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT band, name, rank
            FROM converse.relation_band_catalog()
            ORDER BY band
            """,
            static r => new RelationBandCatalogRow(
                r.GetInt32(0), r.GetString(1), r.GetDouble(2)),
            ct: ct, label: "relation_band_catalog", onError: onError);
}
