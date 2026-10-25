using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record ResolvedRefRow(byte[] Id, string Label);

    /// <summary>Bind the pair jointly through witnessed Native tuple responders.</summary>
    public static async Task<(byte[] X, byte[] Y, string? XContext, string? YContext)> CoupleTopicsAsync(
        global::Npgsql.NpgsqlDataSource dataSource, byte[] x, byte[] y,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource,
            SqlCatalog.Get("conversation.couple_topics"),
            static r => (r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            p => { p.AddWithValue("x", x); p.AddWithValue("y", y); },
            ct: ct, onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? (x, y, null, null) : rows[0];
    }

    /// <summary>
    /// RESOLVE for one external reference, returning the entity id and its display label
    /// from one server command.
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
