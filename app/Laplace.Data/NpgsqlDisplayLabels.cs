using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Bounded display labels and entity facets realized via canonical <c>public.laplace_labels</c>
/// and <c>public.laplace_entities</c>.
/// </summary>
public static class NpgsqlDisplayLabels
{
    public readonly record struct DisplayLabelRow(string IdHex, string Label, short? Tier);
    public readonly record struct DisplayFacetRow(short Tier, string? Type, bool Exists, byte[]? TypeId);

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

    public static async Task<DisplayFacetRow?> FacetAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("entity.facets"),
            static r => (Tier: r.GetInt16(1), Type: r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2)),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { id }),
            timeoutSeconds: 10, ct: ct, onError: onError).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        if (rows[0].Type is null) return new DisplayFacetRow(rows[0].Tier, null, true, null);
        var labels = await NpgsqlRead.ReadRowsAsync(conn,
            "SELECT public.laplace_label_array($1::bytea[], true)",
            static r => r.GetFieldValue<string[]>(0),
            p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { rows[0].Type! }),
            timeoutSeconds: 10, ct: ct, label: "facet_type_labels", onError: onError).ConfigureAwait(false);
        var types = labels.Single();
        if (types.Length != rows.Count)
            throw new InvalidOperationException("Facet type labels lost input positions.");
        return new DisplayFacetRow(rows[0].Tier, string.IsNullOrEmpty(types[0]) ? "Entity" : types[0], true, rows[0].Type);
    }
}
