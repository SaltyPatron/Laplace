using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;

namespace Laplace.Cli;

internal static class ConsensusReExport
{
    internal sealed record TableArena(float[] Cells, int Rows, int Cols, long Relations);

    private static readonly double ModelWeight =
        Laplace.Decomposers.Abstractions.RelationTypeRegistry.Resolve("EMBEDS").Rank
        * Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe;
    private static long PhiFp() => (long)((350.0 + (30.0 - 350.0) * ModelWeight) * 1e9);

    internal static Laplace.SubstrateCRUD.Npgsql.CalibratedInverse NewInverse(NpgsqlDataSource ds) =>
        new(PhiFp());

    internal static async Task<TableArena> ReadTableArenaAsync(
        NpgsqlDataSource ds, Hash128 typeId, int rows, int cols, bool rowsAreOut,
        Func<Hash128, IReadOnlyList<int>?> inIndex,
        Func<Hash128, IReadOnlyList<int>?> outIndex,
        double m)
    {
        var cells = new float[(long)rows * cols];
        long relations = 0;
        await using var conn = await ds.OpenConnectionAsync();
        var inverse = NewInverse(ds);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT subject_id, object_id, rating, witness_count "
            + "FROM laplace.consensus_export_relations($1)";
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var subjIdx = inIndex(FromBytes((byte[])rdr[0]));
            var objIdx  = outIndex(FromBytes((byte[])rdr[1]));
            if (subjIdx is null || objIdx is null) continue;
            float v = (float)(inverse.Wom(rdr.GetInt64(2), rdr.GetInt64(3)) * m);
            if (v == 0f) continue;
            relations++;
            foreach (int i in subjIdx)
                foreach (int o in objIdx)
                {
                    int row = rowsAreOut ? o : i;
                    int col = rowsAreOut ? i : o;
                    if ((uint)row < (uint)rows && (uint)col < (uint)cols)
                        cells[(long)row * cols + col] = v;
                }
        }
        return new TableArena(cells, rows, cols, relations);
    }

    internal static async Task<float[]> ReadNormVectorAsync(
        NpgsqlDataSource ds, Hash128 typeId, int dModel,
        Func<Hash128, IReadOnlyList<int>?> channelIndex, double m)
    {
        var vec = new float[dModel];
        Array.Fill(vec, 1.0f);
        await using var conn = await ds.OpenConnectionAsync();
        var inverse = NewInverse(ds);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT subject_id, rating, witness_count "
            + "FROM laplace.consensus_export_unary($1)";
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var idx = channelIndex(FromBytes((byte[])rdr[0]));
            if (idx is null) continue;
            float v = (float)(inverse.Wom(rdr.GetInt64(1), rdr.GetInt64(2)) * m);
            foreach (int c in idx)
                if ((uint)c < (uint)dModel) vec[c] = v;
        }
        return vec;
    }

    internal static double MoldArenaScale(
        Dictionary<string, SafetensorsContainerParser.TensorReference>? refMap,
        IEnumerable<string> instanceNames)
    {
        if (refMap is null) return 1.0;
        double sumsq = 0; long n = 0;
        foreach (var name in instanceNames)
        {
            if (!refMap.TryGetValue(name, out var tref)) continue;
            long elems = (long)tref.Shape[0] * (tref.Shape.Length > 1 ? tref.Shape[1] : 1);
            var w = WeightTensorETL.LoadTensorF32(refMap, name, elems);
            for (long i = 0; i < w.LongLength; i++) { double v = w[i]; sumsq += v * v; }
            n += elems;
        }
        return n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;
    }

    internal static byte[] ToBf16Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 2];
        for (long i = 0; i < data.LongLength; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(data[i]);
            ushort bf = (ushort)(bits >> 16);
            o[i * 2]     = (byte)bf;
            o[i * 2 + 1] = (byte)(bf >> 8);
        }
        return o;
    }

    internal static byte[] ToF32Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 4];
        Buffer.BlockCopy(data, 0, o, 0, o.Length);
        return o;
    }

    private static unsafe Hash128 FromBytes(byte[] bts)
    {
        if (bts.Length < 16) return Hash128.Zero;
        fixed (byte* p = bts) return *(Hash128*)p;
    }
}
