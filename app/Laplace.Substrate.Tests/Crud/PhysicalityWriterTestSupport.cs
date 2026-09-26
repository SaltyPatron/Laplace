using global::Npgsql;
using System.Buffers.Binary;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Assertions for canonical physicality rows.
/// Descriptor helpers remain only for explicit legacy/readback compatibility tests;
/// ordinary apply must not manufacture descriptor graphs.</summary>
internal static class PhysicalityWriterTestSupport
{
    internal static async Task AssertDescriptorShapesAsync(NpgsqlDataSource dataSource,
        (Hash128 Descriptor, Hash128 Entity, PhysicalityType Type, int Count)[] expected)
    {
        await AssertSelectedRowsAsync(dataSource, expected.Select(row => row.Descriptor),
            expected.Select(row => PhysicalityId.Compute(row.Descriptor, PhysicalityType.DescriptorRetention)), []);
        await using var command = DescriptorReadCommand(dataSource, expected.Select(row => row.Descriptor));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var descriptors = reader.GetFieldValue<byte[][]>(0);
        var entities = reader.GetFieldValue<byte[][]>(1);
        var types = reader.GetFieldValue<short[]>(2);
        var counts = reader.GetFieldValue<int[]>(6);
        Assert.Equal(expected.Length, descriptors.Length);
        Assert.Equal(expected.Length, entities.Length);
        Assert.Equal(expected.Length, types.Length);
        Assert.Equal(expected.Length, counts.Length);
        for (int i = 0; i < expected.Length; ++i)
        {
            Assert.Equal(expected[i].Descriptor.ToBytes(), descriptors[i]);
            Assert.Equal(expected[i].Entity.ToBytes(), entities[i]);
            Assert.Equal((short)expected[i].Type, types[i]);
            Assert.Equal(expected[i].Count, counts[i]);
        }
        Assert.False(await reader.ReadAsync());
    }

    private static NpgsqlCommand DescriptorReadCommand(NpgsqlDataSource dataSource, IEnumerable<Hash128> descriptors)
    {
        var command = dataSource.CreateCommand(SqlCatalog.Get("readback.physicality_descriptors").Text);
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            descriptors.Select(id => id.ToBytes()).ToArray());
        long budget = IngestSizing.ResolveWorkingSetBudgetBytes();
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, budget);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, 512);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, budget / MemoryTopology.Hash128Bytes);
        return command;
    }

    internal static async Task AssertPhysicalityReadbackAsync(
        NpgsqlDataSource dataSource, Hash128 physicalityId, PhysicalityRow expected)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT entity_id,type,
                   public.ST_X(coord),public.ST_Y(coord),
                   public.ST_Z(coord),public.ST_M(coord),
                   hilbert_index,
                   CASE WHEN trajectory IS NULL THEN NULL
                        ELSE generation.trajectory_bits(
                            public.ST_AsBinary(trajectory,'NDR')) END,
                   n_constituents,alignment_residual,source_dim
            FROM laplace.physicalities
            WHERE id=$1
            """);
        command.Parameters.AddWithValue(physicalityId.ToBytes());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.EntityId.ToBytes(), reader.GetFieldValue<byte[]>(0));
        Assert.Equal((short)expected.Type, reader.GetInt16(1));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.CoordX),
            BitConverter.DoubleToInt64Bits(reader.GetDouble(2)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.CoordY),
            BitConverter.DoubleToInt64Bits(reader.GetDouble(3)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.CoordZ),
            BitConverter.DoubleToInt64Bits(reader.GetDouble(4)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.CoordM),
            BitConverter.DoubleToInt64Bits(reader.GetDouble(5)));
        Assert.Equal(expected.HilbertIndex.ToByteArray(),
            reader.GetFieldValue<byte[]>(6));
        Assert.Equal(expected.TrajectoryXyzm is null ? null : Bits(expected.TrajectoryXyzm),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7));
        Assert.Equal(expected.NConstituents, reader.GetInt32(8));
        if (expected.AlignmentResidual is { } residual)
        {
            Assert.False(reader.IsDBNull(9));
            Assert.Equal(BitConverter.DoubleToInt64Bits(residual),
                BitConverter.DoubleToInt64Bits(reader.GetDouble(9)));
        }
        else
            Assert.True(reader.IsDBNull(9));
        if (expected.SourceDim is { } sourceDim)
        {
            Assert.False(reader.IsDBNull(10));
            Assert.Equal(sourceDim, reader.GetInt32(10));
        }
        else
            Assert.True(reader.IsDBNull(10));
        Assert.False(await reader.ReadAsync());
    }

    internal static async Task AssertDescriptorReadbackAsync(NpgsqlDataSource dataSource,
        Hash128 descriptor, PhysicalityRow expected)
    {
        await using var command = DescriptorReadCommand(dataSource, [descriptor]);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(descriptor.ToBytes(), Assert.Single(reader.GetFieldValue<byte[][]>(0)));
        Assert.Equal(expected.EntityId.ToBytes(), Assert.Single(reader.GetFieldValue<byte[][]>(1)));
        Assert.Equal((short)expected.Type, Assert.Single(reader.GetFieldValue<short[]>(2)));
        Assert.Equal(Bits([expected.CoordX, expected.CoordY, expected.CoordZ, expected.CoordM]),
            Assert.Single(reader.GetFieldValue<byte[][]>(3)));
        Assert.Equal(expected.HilbertIndex.ToByteArray(), Assert.Single(reader.GetFieldValue<byte[][]>(4)));
        Assert.Equal(expected.TrajectoryXyzm is null ? null : Bits(expected.TrajectoryXyzm),
            Assert.Single(reader.GetFieldValue<byte[]?[]>(5)));
        Assert.Equal(expected.NConstituents, Assert.Single(reader.GetFieldValue<int[]>(6)));
        Assert.Equal(expected.AlignmentResidual is { } residual ? Bits([residual]) : null,
            Assert.Single(reader.GetFieldValue<byte[]?[]>(7)));
        Assert.Equal(expected.SourceDim, Assert.Single(reader.GetFieldValue<int?[]>(8)));
        Assert.False(await reader.ReadAsync());
    }

    private static byte[] Bits(double[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(double))];
        for (int i = 0; i < values.Length; ++i)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(double)),
                BitConverter.DoubleToInt64Bits(values[i]));
        return bytes;
    }

    internal static void AssertAttempts(
        ApplyResult result, int entities, int physicalities, int attestations)
    {
        Assert.Equal(entities, result.EntitiesAttempted);
        Assert.Equal(physicalities, result.PhysicalitiesAttempted);
        Assert.Equal(attestations, result.AttestationsAttempted);
    }

    internal static async Task AssertSelectedRowsAsync(NpgsqlDataSource dataSource,
        IEnumerable<Hash128> entities, IEnumerable<Hash128> physicalities,
        IEnumerable<Hash128> attestations)
    {
        byte[][] e = entities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] p = physicalities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] a = attestations.Distinct().Select(id => id.ToBytes()).ToArray();
        Assert.Equal(e.LongLength, await CountAsync("entities", e));
        Assert.Equal(p.LongLength, await CountAsync("physicalities", p));
        Assert.Equal(a.LongLength, await CountAsync("attestations", a));

        // Counted in slices: one bound array of every id would exceed a statement's limits.
        async Task<long> CountAsync(string table, byte[][] ids)
        {
            long total = 0;
            for (int start = 0; start < ids.Length; start += 50_000)
            {
                await using var command = dataSource.CreateCommand(
                    $"SELECT count(DISTINCT id) FROM laplace.{table} WHERE id=ANY($1::bytea[])");
                command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    ids[start..Math.Min(ids.Length, start + 50_000)]);
                total += (long)(await command.ExecuteScalarAsync())!;
            }
            return total;
        }
    }
}
