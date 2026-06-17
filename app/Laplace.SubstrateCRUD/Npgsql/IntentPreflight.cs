using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;







internal static class IntentPreflight
{
    private const int ChunkSize = 250_000;

    
    
    
    
    
    public static async Task<(HashSet<Hash128> Entities, HashSet<Hash128> Phys, HashSet<Hash128> Att)>
        RunAsync(
            NpgsqlConnection conn,
            IReadOnlyList<Hash128> entityIds,
            IReadOnlyList<Hash128> physIds,
            IReadOnlyList<Hash128> attIds,
            CancellationToken ct)
    {
        var entExisting = new HashSet<Hash128>();
        var physExisting = new HashSet<Hash128>();
        var attExisting = new HashSet<Hash128>();
        if (entityIds.Count == 0 && physIds.Count == 0 && attIds.Count == 0)
            return (entExisting, physExisting, attExisting);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "SELECT (p).entity_exists, (p).phys_exists, (p).att_exists " +
            "FROM laplace.intent_preflight(@ent, @phys, @att) p";
        var entParam = cmd.Parameters.Add(
            new NpgsqlParameter("ent", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        var physParam = cmd.Parameters.Add(
            new NpgsqlParameter("phys", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        var attParam = cmd.Parameters.Add(
            new NpgsqlParameter("att", NpgsqlDbType.Array | NpgsqlDbType.Bytea));

        int max = Math.Max(entityIds.Count, Math.Max(physIds.Count, attIds.Count));
        for (int off = 0; off < max; off += ChunkSize)
        {
            int entLen = Math.Min(ChunkSize, Math.Max(0, entityIds.Count - off));
            int physLen = Math.Min(ChunkSize, Math.Max(0, physIds.Count - off));
            int attLen = Math.Min(ChunkSize, Math.Max(0, attIds.Count - off));

            entParam.Value = ToByteaArray(entityIds, off, entLen);
            physParam.Value = ToByteaArray(physIds, off, physLen);
            attParam.Value = ToByteaArray(attIds, off, attLen);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            DecodeBitmap(entExisting, entityIds, off, entLen, r.GetFieldValue<byte[]>(0));
            DecodeBitmap(physExisting, physIds, off, physLen, r.GetFieldValue<byte[]>(1));
            DecodeBitmap(attExisting, attIds, off, attLen, r.GetFieldValue<byte[]>(2));
        }
        return (entExisting, physExisting, attExisting);
    }

    
    
    
    
    public static async Task<HashSet<Hash128>> EntitiesExistAsync(
        NpgsqlConnection conn, IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var existing = new HashSet<Hash128>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT laplace.entities_exist_bitmap(@ids)";
        var idsParam = cmd.Parameters.Add(
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        for (int off = 0; off < ids.Count; off += ChunkSize)
        {
            int len = Math.Min(ChunkSize, ids.Count - off);
            idsParam.Value = ToByteaArray(ids, off, len);
            var res = await cmd.ExecuteScalarAsync(ct);
            var bitmap = res as byte[] ?? Array.Empty<byte>();
            DecodeBitmap(existing, ids, off, len, bitmap);
        }
        return existing;
    }

    
    public static List<Hash128> CollectUnprovenIds<TRow>(
        IReadOnlyList<SubstrateChange> changes,
        Func<SubstrateChange, System.Collections.Immutable.ImmutableArray<TRow>> select,
        Func<TRow, Hash128> idOf,
        ProvenIdCache proven)
    {
        var seen = new HashSet<Hash128>();
        var ids = new List<Hash128>();
        foreach (var c in changes)
            foreach (var row in select(c))
            {
                var id = idOf(row);
                if (!proven.Contains(id) && seen.Add(id)) ids.Add(id);
            }
        return ids;
    }

    private static byte[][] ToByteaArray(IReadOnlyList<Hash128> ids, int off, int len)
    {
        var arg = new byte[len][];
        for (int i = 0; i < len; i++) arg[i] = ids[off + i].ToBytes();
        return arg;
    }

    private static void DecodeBitmap(
        HashSet<Hash128> existing, IReadOnlyList<Hash128> ids, int off, int len, byte[] bitmap)
    {
        for (int i = 0; i < len; i++)
        {
            byte b = (byte)(i >> 3 < bitmap.Length ? bitmap[i >> 3] : 0);
            if (((b >> (i & 7)) & 1) != 0) existing.Add(ids[off + i]);
        }
    }
}
