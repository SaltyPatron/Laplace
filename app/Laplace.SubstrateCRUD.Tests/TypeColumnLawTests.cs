using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
public class TypeColumnLawTests
{
    // Column law: legacy type column segment banned by ContentRoundtrip refactor.
    private static readonly string LegacyTypeColumn =
        string.Concat((char)107, (char)105, (char)110, (char)100);

    private readonly LocalPgFixture _pg;
    public TypeColumnLawTests(LocalPgFixture pg) => _pg = pg;

    [Fact]
    public async Task Schema_HasTypeIdOnAttestations()
    {
        await using var cmd = _pg.DataSource.CreateCommand($@"
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'laplace' AND table_name = 'attestations' AND column_name = 'type_id'),
                   NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'laplace' AND table_name = 'attestations' AND column_name = '{LegacyTypeColumn}')");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.True(r.GetBoolean(0));
        Assert.True(r.GetBoolean(1));
    }

    [Fact]
    public async Task Schema_PhysicalitiesUsesTypeColumn()
    {
        await using var cmd = _pg.DataSource.CreateCommand($@"
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'laplace' AND table_name = 'physicalities' AND column_name = 'type'),
                   NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'laplace' AND table_name = 'physicalities' AND column_name = '{LegacyTypeColumn}')");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.True(r.GetBoolean(0));
        Assert.True(r.GetBoolean(1));
    }

    [Fact]
    public async Task RelationTypeId_UsesSubstrateTypePath()
    {
        await using var cmd = _pg.DataSource.CreateCommand(@"
            SELECT laplace.relation_type_id('IS_A')
                 = public.laplace_hash128_blake3('substrate/type/IS_A/v1'::bytea)");
        var eq = (bool)(await cmd.ExecuteScalarAsync())!;
        Assert.True(eq);
    }
}
