using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class PerfcacheReaderTests
{
    [Fact]
    public async Task MappedFloorResolvesThroughEveryExistenceReaderWithoutADatabase()
    {
        CodepointPerfcache.LoadDefault();
        // Any attempted database access fails: the data source is already disposed.
        var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused;Username=unused");
        var reader = new NpgsqlSubstrateReader(source);
        await source.DisposeAsync();
        Hash128[] ids = [
            CodepointPerfcache.Records['A'].Hash,
            CodepointPerfcache.Records[' '].Hash,
            CodepointPerfcache.Records['狼'].Hash,
            CodepointPerfcache.Records['A'].Hash,
            CodepointPerfcache.Records[0].Hash];

        Assert.Equal(new byte[] { 31 }, await reader.EntitiesExistBitmapAsync(ids));
        Assert.Equal(new byte[] { 31 }, await reader.TierBatchExistenceProbeAsync(ids, 0));
        Assert.Equal(new byte[] { 31 }, await reader.ContentDescentBitmapAsync(ids, [-1, -1, -1, -1, -1]));
    }

    [Fact]
    public void FloorMembershipLeavesUnknownsUnresolvedAndPreservesRepeatedIds()
    {
        CodepointPerfcache.LoadDefault();
        var a = CodepointPerfcache.Records['A'].Hash;
        var unknown = new Hash128(0x1234, 0x5678);
        Assert.Equal(new byte[] { 5 }, CodepointPerfcache.KnownIdsBitmap([a, unknown, a]));
    }
}
