using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    /// <summary><c>realize.vertex_tier(flags)</c> / <c>realize.vertex_atom(flags)</c>.</summary>
    public static async Task<(short Tier, int Atom)> VertexDecodeAsync(
        NpgsqlDataSource dataSource, long flags, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("realize.vertex_decode"),
            static r => (r.GetInt16(0), r.GetInt32(1)),
            p => p.AddWithValue("flags", flags),
            ct: ct, label: "vertex_tier", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? ((short)0, 0) : rows[0];
    }

    public readonly record struct EntityPlacementRow(
        short Type, double X, double Y, double Z, double M, double Radius, int Constituents);

    private static EntityPlacementRow MapPlacement(NpgsqlDataReader r) => new(
        r.GetInt16(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
        r.GetDouble(4), r.GetDouble(5), r.GetInt32(6));

    /// <summary>
    /// <c>ops.entity_physicalities(id)</c> — every physicality of one entity, ordered by
    /// physicality type.
    /// </summary>
    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.entity_physicalities"),
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_physicalities", onError: onError);

    /// <inheritdoc cref="EntityPhysicalitiesAsync(NpgsqlConnection, byte[], CancellationToken, NpgsqlRead.ErrorTranslator?)"/>
    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("ops.entity_physicalities"),
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_physicalities", onError: onError);

    /// <summary>The entity's lowest-type physicality only, used as its placement anchor.</summary>
    public static async Task<EntityPlacementRow?> EntityPrimaryFormAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ops.entity_primary_form"),
            MapPlacement,
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "entity_primary_form", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct OrdinalPlacementRow(
        long Ordinal, double X, double Y, double Z, double M, double Radius, int Constituents);

    /// <summary>
    /// Lowest-type physicality per id for a batch in one round trip. Ordinals are
    /// 1-based input positions (WITH ORDINALITY); ids with no physicality have no row.
    /// </summary>
    public static Task<IReadOnlyList<OrdinalPlacementRow>> EntityPrimaryFormsBatchAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.entity_primary_forms_batch"),
            static r => new OrdinalPlacementRow(
                r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
                r.GetDouble(4), r.GetDouble(5), r.GetInt32(6)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "entity_primary_forms_batch", onError: onError);

    public readonly record struct ConstellationSampleRow(
        int Ordinal, string IdHex, short PhysicalityType,
        double X, double Y, double Z, double M, double Radius,
        int Constituents, string HilbertHex);

    /// <summary>Native entities sampled
    /// across Hilbert order.</summary>
    public static Task<IReadOnlyList<ConstellationSampleRow>> ConstellationSampleAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("structural.constellation_sample"),
            static r => new ConstellationSampleRow(
                r.GetInt32(0), r.GetString(1), r.GetInt16(2),
                r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                r.GetDouble(7), r.GetInt32(8), r.GetString(9)),
            p => p.AddWithValue("limit", Math.Max(0, limit)),
            ct: ct, label: "constellation_sample", onError: onError);

    public readonly record struct EntityConstituentRow(
        int Ordinal, string ChildIdHex, int RunLength, long Flags, string ChildLabel);

    /// <summary><c>realize.constituents(id)</c>, lossless for every constituent.</summary>
    public static Task<IReadOnlyList<EntityConstituentRow>> ConstituentsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("realize.constituents_rendered"),
            static r => new EntityConstituentRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "constituents", onError: onError);

    /// <summary>
    /// One vertex of a packed trajectory: the raw XYZM carrier values and what they unpack
    /// to (child id, ordinal, run length, flags). The XYZM values encode the manifest; they
    /// are not the child's position and are not geometry for curve comparison.
    /// </summary>
    public readonly record struct PackedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, int RunLength, long Flags);

    public static Task<IReadOnlyList<PackedTrajectoryVertexRow>> PackedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.packed_trajectory_vertices"),
            static r => new PackedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetInt32(6), r.GetInt64(7)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "packed_trajectory_vertices", onError: onError);

    /// <summary>
    /// One vertex of a realized curve: each constituent's own coordinate physicality,
    /// read in constituent ordinal order.
    /// </summary>
    public readonly record struct RealizedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, string ChildLabel, double Radius);

    public static Task<IReadOnlyList<RealizedTrajectoryVertexRow>> RealizedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.realized_trajectory_vertices"),
            static r => new RealizedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetString(6), r.GetDouble(7)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "realized_trajectory_vertices", onError: onError);

    /// <summary>Exact <c>realize.render_text_batch(ids)</c>.</summary>
    public static async Task<string[]?> RenderTextBatchAsync(
        NpgsqlDataSource dataSource, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var scalar = await NpgsqlRead.ExecuteScalarAsync<object>(dataSource, SqlCatalog.Get("realize.render_text_batch"),
            p =>
            {
                var parameter = p.AddWithValue("ids", ids);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "render_text_batch", onError: onError).ConfigureAwait(false);
        return scalar as string[];
    }
}
