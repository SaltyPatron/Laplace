namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record ResolvedRefRow(byte[] Id, string Label);

    /// <summary>
    /// Resolve one external reference and materialize its display label in the same
    /// server command. This is the set-wise replacement for leasing one connection,
    /// resolving through a second connection, then returning to the first for labeling.
    /// </summary>
    public static Task<ResolvedRefRow?> ResolveRefWithLabelAsync(
        global::Npgsql.NpgsqlDataSource dataSource,
        string reference,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadFirstOrDefaultAsync(dataSource, """
            WITH resolved AS (
                SELECT converse.resolve_ref(@ref) AS id
            )
            SELECT r.id,
                   COALESCE(NULLIF(realize.render_text_fast(r.id, 8), ''),
                            converse.label_or_hex(r.id)) AS label
            FROM resolved AS r
            WHERE r.id IS NOT NULL
            """,
            static r => new ResolvedRefRow(r.GetFieldValue<byte[]>(0), r.GetString(1)),
            p => p.AddWithValue("ref", reference),
            ct: ct, label: "resolve_ref_with_label", onError: onError);
}
