using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Display labels for a bounded id set, realized by the installed <c>display.labels</c>
/// catalog statement so every surface shows the same label for the same entity.
///
/// Identity and display stay separate: the caller keeps the canonical id, and the label is
/// a realization of it. When no readable realization exists the label carries a short
/// form of the canonical id rather than generic unresolved text.
/// </summary>
public static class NpgsqlDisplayLabels
{
    public readonly record struct DisplayLabelRow(string IdHex, string Label, short? Tier);
    public readonly record struct DisplayFacetRow(short Tier, string Type, bool Exists, byte[] TypeId);

    private static DisplayLabelRow MapDisplayLabel(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2));

    private static void BindIds(NpgsqlParameterCollection parameters, byte[][] ids)
    {
        var param = parameters.AddWithValue("ids", ids);
        param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
    }

    public static Task<IReadOnlyList<DisplayLabelRow>> ReadAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            MapDisplayLabel,
            p => BindIds(p, ids),
            timeoutSeconds: 30, ct: ct, label: "display_labels", onError: onError);

    public static Task<IReadOnlyList<DisplayLabelRow>> ReadAsync(
        NpgsqlDataSource dataSource, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("display.labels"),
            MapDisplayLabel,
            p => BindIds(p, ids),
            timeoutSeconds: 30, ct: ct, label: "display_labels", onError: onError);

    public static async Task<DisplayLabelRow?> ReadOneAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await ReadAsync(conn, [id], ct, onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<DisplayLabelRow?> ReadOneAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await ReadAsync(dataSource, [id], ct, onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Entity tier, type label and existence without realizing the entity body, so a
    /// high-tier entity is not reconstructed just to learn its facets. Display text comes
    /// from <c>ReadAsync</c>.
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
        return new DisplayFacetRow(rows[0].Tier, string.IsNullOrEmpty(types[0]) ? "Entity" : types[0], true, rows[0].Type);
    }
}
