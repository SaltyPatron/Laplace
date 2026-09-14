using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record ResolvedRefRow(byte[] Id, string Label);

    /// <summary>
    /// Resolve one external reference and materialize its display label in the same
    /// server command. This is the set-wise replacement for leasing one connection,
    /// resolving through a second connection, then returning to the first for labeling.
    /// The SQL is owned by the native catalog, not this managed caller.
    /// </summary>
    public static Task<ResolvedRefRow?> ResolveRefWithLabelAsync(
        global::Npgsql.NpgsqlDataSource dataSource,
        string reference,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadFirstOrDefaultAsync(dataSource, SqlCatalog.Get("resolve.ref_with_label"),
            static r => new ResolvedRefRow(r.GetFieldValue<byte[]>(0), r.GetString(1)),
            p => p.AddWithValue("ref", reference),
            ct: ct, label: "resolve_ref_with_label", onError: onError);
}
