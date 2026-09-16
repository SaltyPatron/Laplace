using global::Npgsql;
using System.Buffers.Binary;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Assertions distinguish source rows from the native descriptor graphs
/// that the same normal apply now admits. No formula predicts generated row counts.</summary>
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

    internal static PhysicalityAdmissionReceipt AssertAttempts(
        ApplyResult result, int entities, int physicalities, int attestations, long sourceForms)
    {
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(result.PhysicalityAdmission);
        Assert.Equal(sourceForms, receipt.SourceForms);
        Assert.Equal(entities + receipt.GeneratedEntityRows, result.EntitiesAttempted);
        Assert.Equal(physicalities + receipt.GeneratedPhysicalityRows, result.PhysicalitiesAttempted);
        Assert.Equal(attestations + receipt.GeneratedAttestationRows, result.AttestationsAttempted);
        return receipt;
    }

    internal static async Task AssertSelectedRowsAsync(NpgsqlDataSource dataSource,
        IEnumerable<Hash128> entities, IEnumerable<Hash128> physicalities,
        IEnumerable<Hash128> attestations)
    {
        byte[][] e = entities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] p = physicalities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] a = attestations.Distinct().Select(id => id.ToBytes()).ToArray();
        await using var command = dataSource.CreateCommand("""
            SELECT (SELECT count(DISTINCT id) FROM laplace.entities WHERE id=ANY($1::bytea[])),
                   (SELECT count(*) FROM laplace.physicalities WHERE id=ANY($2::bytea[])),
                   (SELECT count(*) FROM laplace.attestations WHERE id=ANY($3::bytea[]))
            """);
        foreach (var ids in new[] { e, p, a })
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(e.LongLength, reader.GetInt64(0));
        Assert.Equal(p.LongLength, reader.GetInt64(1));
        Assert.Equal(a.LongLength, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }
}
