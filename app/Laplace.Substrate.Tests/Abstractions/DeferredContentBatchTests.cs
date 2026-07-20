using System.Linq;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;








[Collection("GrammarPerfcache")]
public sealed class DeferredContentBatchTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "DeferredContent");

    private sealed class FakeReader : ISubstrateReader
    {
        private readonly bool _present;
        public FakeReader(bool present) => _present = present;
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            var bm = new byte[(candidates.Count + 7) / 8];
            if (_present)
                for (int i = 0; i < candidates.Count; i++)
                    bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }

    private static int ContentEntityCount(SubstrateChange change) =>
        change.IntentStages.Where(s => s is { IsInvalid: false }).Sum(s => s.EntityCount);

    [Theory]
    [InlineData("dog")]
    [InlineData("hello world")]
    [InlineData("a longer example sentence, with punctuation.")]
    public async Task NullReader_StagesImmediately_Unfiltered(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        var b = new SubstrateChangeBuilder(Src, "test/null").EnableDeferredContent(null);
        Assert.Null(b.DeferredContent);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(b, bytes, Src, out var root));
        Assert.NotEqual(default, root);
        var change = await b.SetInputUnitsConsumed(1).BuildAsync();
        Assert.True(ContentEntityCount(change) > 0);
    }

    [Theory]
    [InlineData("dog")]
    [InlineData("hello world")]
    [InlineData("a longer example sentence, with punctuation.")]
    public async Task PresentBitmap_DefersAndStagesZero(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        var reader = new FakeReader(present: true);
        var b = new SubstrateChangeBuilder(Src, "test/present").EnableDeferredContent(reader);
        Assert.NotNull(b.DeferredContent);


        Assert.True(ContentTierSpine.TryStageIntoBuilder(b, bytes, Src, out var root));
        Assert.NotEqual(default, root);

        var change = await b.SetInputUnitsConsumed(1).BuildAsync();
        Assert.Equal(0, ContentEntityCount(change));
    }

    [Theory]
    [InlineData("dog")]
    [InlineData("hello world")]
    [InlineData("a longer example sentence, with punctuation.")]
    public async Task AbsentBitmap_MatchesNullReader(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);

        var baseB = new SubstrateChangeBuilder(Src, "test/base").EnableDeferredContent(null);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(baseB, bytes, Src, out var baseRoot));
        var baseChange = await baseB.SetInputUnitsConsumed(1).BuildAsync();
        int baseEntities = ContentEntityCount(baseChange);
        Assert.True(baseEntities > 0);

        var reader = new FakeReader(present: false);
        var b = new SubstrateChangeBuilder(Src, "test/absent").EnableDeferredContent(reader);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(b, bytes, Src, out var root));
        var change = await b.SetInputUnitsConsumed(1).BuildAsync();

        Assert.True(baseRoot.EqualsBytewise(root),
            $"root diverged for '{s}': null-reader={baseRoot} deferred={root}");
        Assert.Equal(baseEntities, ContentEntityCount(change));
    }

    [Fact]
    public async Task DuplicateContentInBatch_BuildsTreeOnce_StillStages()
    {


        var reader = new FakeReader(present: false);
        var b = new SubstrateChangeBuilder(Src, "test/dup").EnableDeferredContent(reader);

        Assert.True(ContentTierSpine.TryStageUnderscoredIntoBuilder(
            b, Encoding.UTF8.GetBytes("hot_dog"), Src, out var r1));
        Assert.True(ContentTierSpine.TryStageUnderscoredIntoBuilder(
            b, Encoding.UTF8.GetBytes("hot_dog"), Src, out var r2));
        Assert.True(r1.EqualsBytewise(r2));

        var change = await b.SetInputUnitsConsumed(1).BuildAsync();
        Assert.True(ContentEntityCount(change) > 0);
    }
}
