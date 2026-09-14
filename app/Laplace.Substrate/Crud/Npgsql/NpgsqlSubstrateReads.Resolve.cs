using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record ResolvedRefRow(byte[] Id, string Label);

    /// <summary>
    /// Resolve one external reference and materialize its display label in the same
    /// server command. This is the set-wise replacement for leasing one connection,
    /// resolving through a second connection, then returning to the first for labeling.
    /// </summary>
    public static async Task<ResolvedRefRow?> ResolveRefWithLabelAsync(
        global::Npgsql.NpgsqlDataSource dataSource,
        string reference,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource,
            SqlCatalog.Get("conversation.resolve_ref_with_label"),
            static r => new ResolvedRefRow(r.GetFieldValue<byte[]>(0), r.GetString(1)),
            p => p.Add("ref", NpgsqlDbType.Text).Value = reference,
            ct: ct,
            onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}
