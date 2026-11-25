using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct EntityPlacementRow(
        short Type, double X, double Y, double Z, double M, double Radius, int Constituents);

    private static EntityPlacementRow MapPlacement(NpgsqlDataReader r) => new(
        r.GetInt16(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
        r.GetDouble(4), r.GetDouble(5), r.GetInt32(6));

    private const string EntityPhysicalitiesSql = """
        SELECT 0::smallint,
               ST_X(e.coord), ST_Y(e.coord), ST_Z(e.coord), ST_M(e.coord),
               sqrt(ST_X(e.coord)*ST_X(e.coord) + ST_Y(e.coord)*ST_Y(e.coord) + ST_Z(e.coord)*ST_Z(e.coord) + ST_M(e.coord)*ST_M(e.coord)),
               coalesce(ST_NumPoints(p.path), 1)
        FROM public.laplace_entities(ARRAY[public.blake3_from_bytea($1)]) e
        LEFT JOIN public.laplace_physicalities(ARRAY[public.blake3_from_bytea($1)]) p ON p.entity = e.id
        WHERE e.id IS NOT NULL
        """;

    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, EntityPhysicalitiesSql, MapPlacement,
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "entity_physicalities", onError: onError);

    public static Task<IReadOnlyList<EntityPlacementRow>> EntityPhysicalitiesAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, EntityPhysicalitiesSql, MapPlacement,
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "entity_physicalities", onError: onError);

    public readonly record struct ConstellationSampleRow(
        int Ordinal, string IdHex, short PhysicalityType,
        double X, double Y, double Z, double M, double Radius,
        int Constituents, string HilbertHex);

    public static Task<IReadOnlyList<ConstellationSampleRow>> ConstellationSampleAsync(
        NpgsqlConnection conn, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT ord,
                   encode(public.blake3_send(id), 'hex'),
                   0::smallint,
                   x, y, z, m, radius, path_len,
                   lpad(to_hex(hilbert), 16, '0')
            FROM public.laplace_constellation_sample(greatest(1, $1))
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ConstellationSampleRow(
                r.GetInt32(0), r.GetString(1), r.GetInt16(2),
                r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                r.GetDouble(7), r.GetInt32(8), r.GetString(9)),
            p => p.AddWithValue(Math.Max(0, limit)),
            ct: ct, label: "constellation_sample", onError: onError);
    }

    public readonly record struct EntityConstituentRow(
        int Ordinal, string ChildIdHex, int RunLength, long Flags, string ChildLabel);

    public static Task<IReadOnlyList<EntityConstituentRow>> ConstituentsAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT c.ordinal,
                   encode(public.blake3_send(c.child), 'hex'),
                   c.run_length,
                   c.flags,
                   coalesce(public.laplace_label_text(public.blake3_send(c.child), false), encode(public.blake3_send(c.child), 'hex'))
            FROM public.laplace_constituents(ARRAY[public.blake3_from_bytea($1)]) c
            ORDER BY c.ordinal
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new EntityConstituentRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "constituents", onError: onError);
    }

    public readonly record struct PackedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, int RunLength, long Flags);

    public static Task<IReadOnlyList<PackedTrajectoryVertexRow>> PackedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH p AS (
                SELECT path
                FROM public.laplace_physicalities(ARRAY[public.blake3_from_bytea($1)])
                LIMIT 1
            )
            SELECT c.ordinal,
                   coalesce(ST_X(ST_PointN(p.path, c.ordinal)), 0),
                   coalesce(ST_Y(ST_PointN(p.path, c.ordinal)), 0),
                   coalesce(ST_Z(ST_PointN(p.path, c.ordinal)), 0),
                   coalesce(ST_M(ST_PointN(p.path, c.ordinal)), 0),
                   encode(public.blake3_send(c.child), 'hex'),
                   c.run_length,
                   c.flags
            FROM public.laplace_constituents(ARRAY[public.blake3_from_bytea($1)]) c
            LEFT JOIN p ON true
            ORDER BY c.ordinal
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new PackedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetInt32(6), r.GetInt64(7)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "packed_trajectory_vertices", onError: onError);
    }

    public readonly record struct WitnessedContentRow(
        string FileIdHex, string Label, short Tier, long Bytes);

    public static Task<IReadOnlyList<WitnessedContentRow>> ListWitnessedContentAsync(
        NpgsqlDataSource dataSource, byte[] witnessIdBytes, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT encode(public.blake3_send(hc.entity), 'hex') AS file_id_hex,
                   coalesce(public.laplace_label_text(public.blake3_send(hc.entity), false), encode(public.blake3_send(hc.entity), 'hex')) AS label,
                   coalesce(hc.tier, 0::smallint) AS tier,
                   coalesce(octet_length(public.laplace_label_text(public.blake3_send(hc.entity), false)), 0)::bigint AS bytes
            FROM public.laplace_held_closure(
                ARRAY[public.blake3_from_bytea($1)],
                0::smallint, -1::bigint, 4, 2048) hc
            WHERE hc.entity IS NOT NULL
              AND hc.level > 0
              AND (hc.is_file OR (hc.level = 1 AND NOT coalesce(hc.is_metadata, false)))
            ORDER BY hc.is_file DESC, hc.level ASC, hc.node_idx DESC
            LIMIT greatest(1, least($2, 200))
            """;
        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new WitnessedContentRow(
                r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetInt16(2),
                r.IsDBNull(3) ? 0L : r.GetInt64(3)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = witnessIdBytes });
                p.AddWithValue(Math.Clamp(limit, 1, 200));
            },
            timeoutSeconds: 30,
            ct: ct,
            label: "list_witnessed_content",
            onError: onError);
    }

    public readonly record struct RealizedTrajectoryVertexRow(
        int Ordinal, double X, double Y, double Z, double M,
        string ChildIdHex, string ChildLabel, double Radius);

    public static Task<IReadOnlyList<RealizedTrajectoryVertexRow>> RealizedTrajectoryVerticesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH cons AS MATERIALIZED (
                SELECT c.ordinal, c.child
                FROM public.laplace_constituents(ARRAY[public.blake3_from_bytea($1)]) c
                ORDER BY c.ordinal
            ),
            coords AS MATERIALIZED (
                SELECT ec.ordinal, ec.id, ec.x, ec.y, ec.z, ec.m
                FROM public.laplace_entity_coords(ARRAY(SELECT child FROM cons ORDER BY ordinal)) ec
            )
            SELECT cons.ordinal,
                   coalesce(co.x, 0), coalesce(co.y, 0), coalesce(co.z, 0), coalesce(co.m, 0),
                   encode(public.blake3_send(cons.child), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(cons.child), false), encode(public.blake3_send(cons.child), 'hex')),
                   sqrt(coalesce(co.x*co.x + co.y*co.y + co.z*co.z + co.m*co.m, 0))
            FROM cons
            LEFT JOIN coords co ON co.id = cons.child AND co.ordinal = cons.ordinal
            ORDER BY cons.ordinal
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new RealizedTrajectoryVertexRow(
                r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                r.GetString(5), r.GetString(6), r.GetDouble(7)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id }),
            ct: ct, label: "realized_trajectory_vertices", onError: onError);
    }
}
