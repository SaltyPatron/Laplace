using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Human-facing labels for bounded visualization/result sets.
///
/// Identity and display are deliberately separate: the caller keeps the canonical hash id,
/// while this read chooses a Unicode surface that a person can inspect. It never uses a
/// content hash as a label.
///
/// The projection law lives in the installed extension (`realize.display_label_batch`) so
/// every client can share it and pg_regress can prove it. High-tier rendering is
/// containment-owned per GH #804; entity.type_id is descriptive fallback metadata only.
/// </summary>
public static class NpgsqlDisplayLabels
{
    public readonly record struct DisplayLabelRow(string IdHex, string Label, short? Tier);
    public readonly record struct DisplayFacetRow(short Tier, string Type, bool Exists);

    public static Task<IReadOnlyList<DisplayLabelRow>> ReadAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            static r => new DisplayLabelRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            timeoutSeconds: 30, ct: ct, label: "display_labels", onError: onError);

    public static async Task<DisplayLabelRow?> ReadOneAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await ReadAsync(conn, [id], ct, onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Entity tier/type/existence without rendering the entity body. The old explorer facet
    /// helper performed render_text_fast(id, 8) merely to learn these fields, which meant
    /// opening a high-tier entity could reconstruct the document before the page had even
    /// elected what to show. Display text is owned by <see cref="ReadAsync"/> instead.
    /// </summary>
    public static async Task<DisplayFacetRow?> FacetAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("entity.facets"),
            static r => (Tier: r.GetInt16(1), Type: r.GetFieldValue<byte[]>(2)),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { id }),
            timeoutSeconds: 10, ct: ct, onError: onError).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        var labels = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("types.labels"),
            static r => r.GetFieldValue<string[]>(0),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, rows.Select(r => r.Type).ToArray()),
            timeoutSeconds: 10, ct: ct, onError: onError).ConfigureAwait(false);
        var types = labels.Single();
        if (types.Length != rows.Count)
            throw new InvalidOperationException("Facet type labels lost input positions.");
        return new DisplayFacetRow(rows[0].Tier, string.IsNullOrEmpty(types[0]) ? "Entity" : types[0], true);
    }
}
