using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class CanonicalEntitySelectionTests
{
    private static Hash128 H(string value) => Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes(value));

    private static (IntPtr Ptr, long Len) Blob(IntentStage stage)
        => stage.TupleBuffer(IntentStageTable.Entities);

    [Fact]
    public void CanonicalRepresentative_IsIndependentOfStageOrder()
    {
        var id = H("canonical-selection/id");
        var lowType = H("canonical-selection/type-a");
        var highType = H("canonical-selection/type-z");

        using var high = IntentStage.New(2);
        using var low = IntentStage.New(2);
        high.AddEntity(id, 3, highType, H("source-z"));
        low.AddEntity(id, 1, lowType, H("source-a"));

        static (short Tier, Hash128 Type) Pick(
            IReadOnlyList<(IntPtr Ptr, long Len)> blobs)
        {
            var parsed = CopyTupleParser.ParseEntities(blobs);
            var selected = NpgsqlSubstrateWriter.DistinctEntityRowIndices(
                parsed, tier0Gate: false, out var tier0Present);
            Assert.Null(tier0Present);
            Assert.Single(selected);
            int i = selected[0];
            return (parsed.Tiers[i], parsed.TypeIds[i]);
        }

        var forward = Pick([Blob(high), Blob(low)]);
        var reverse = Pick([Blob(low), Blob(high)]);

        Assert.Equal(forward, reverse);
        Assert.Equal((short)1, forward.Tier);
        Assert.Equal(lowType, forward.Type);
    }

    [Fact]
    public void TierZeroInterpretation_ProvesWholeCanonicalIdPresentAfterLayerCompletion()
    {
        var id = H("canonical-selection/tier0-id");
        using var stage = IntentStage.New(2);
        stage.AddEntity(id, 2, H("canonical-selection/word"), H("source-word"));
        stage.AddEntity(id, 0, H("canonical-selection/codepoint"), H("source-codepoint"));

        var parsed = CopyTupleParser.ParseEntities([Blob(stage)]);
        var selected = NpgsqlSubstrateWriter.DistinctEntityRowIndices(
            parsed, tier0Gate: true, out var tier0Present);

        Assert.Empty(selected);
        Assert.NotNull(tier0Present);
        Assert.Equal([id], tier0Present);
    }
}
