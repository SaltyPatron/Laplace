using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

// Read-only acceptance against the selected installed database. This suite
// creates no database, schema, table or service, and uses no fixture backend.
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class Blake3TransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(257)]
    public async Task BinaryScalarAndArrayRetainTheirNativeBytes(int count)
    {
        var bytes = Enumerable.Range(0, 16).Select(i => (byte)(0x80 + i * 7)).ToArray();
        var identity = Hash128.FromBytes(bytes);
        var values = Enumerable.Range(0, count).Select(i =>
        {
            Span<byte> input = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(input, i);
            return Hash128.Blake3(input);
        }).ToArray();
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Serving);
        await using var command = NpgsqlCatalog.Command(source, "verify.blake3_transport", identity, values);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("public.blake3", reader.GetPostgresType(0).FullName);
        Assert.Equal(typeof(Hash128), reader.GetFieldType(0));
        Assert.Equal(identity, reader.GetFieldValue<Hash128>(0));
        Assert.Equal(values, reader.GetFieldValue<Hash128[]>(1));
        Assert.Equal(bytes, reader.GetFieldValue<byte[]>(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task DatabaseNullIsDistinctFromTheZeroIdentity()
    {
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Serving);
        await using (var command = NpgsqlCatalog.Command(source, "verify.blake3_transport", null, null))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }
        await using var zero = NpgsqlCatalog.Command(source, "verify.blake3_transport",
            default(Hash128), Array.Empty<Hash128>());
        await using var zeroReader = await zero.ExecuteReaderAsync();
        Assert.True(await zeroReader.ReadAsync());
        Assert.False(zeroReader.IsDBNull(0));
        Assert.Equal(default, zeroReader.GetFieldValue<Hash128>(0));
        Assert.Equal(new byte[16], zeroReader.GetFieldValue<byte[]>(2));
    }

    [Fact]
    public async Task NullableArrayElementsRetainPositions()
    {
        Hash128 identity = Hash128.FromBytes(new byte[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,255 });
        Hash128?[] values = [identity, null, default(Hash128), identity];
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Serving);
        await using var command = NpgsqlCatalog.Command(source, "verify.blake3_transport", identity, values);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(values, reader.GetFieldValue<Hash128?[]>(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ServerCompositionMatchesNativeAtTheActualArity(int count)
    {
        var values = Enumerable.Range(0, count).Select(i =>
            Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes($"native-transport-{i}"))).ToArray();
        var expected = Hash128.Merkle(2, values);
        await using var source = LaplaceDataSource.Create(SubstrateAccess.Serving);
        await using var command = NpgsqlCatalog.Command(source, "verify.blake3_composition", values);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected, reader.GetFieldValue<Hash128>(0));
    }
}
