using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class FeedbackObservationTests(LocalPgFixture pg)
{
    [Fact]
    public async Task Corrections_TransportReplayPreservesBothWitnesses()
    {
        var subject = Hash128.Text("test/feedback-occurrence/subject");
        var obj = Hash128.Text("test/feedback-occurrence/object");
        var relation = RelationTypeRegistry.RelationTypeId("RELATED_TO");
        await new Laplace.SubstrateCRUD.Npgsql.NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(
            new SubstrateChangeBuilder(FeedbackContent.Source, "test/feedback-occurrence/entities")
                .AddEntity(subject, EntityTier.Word, EntityTypeRegistry.Text)
                .AddEntity(obj, EntityTier.Word, EntityTypeRegistry.Text).Build());
        for (int i = 0; i < 20; ++i) await Deposit(true, $"confirm-{i}");
        for (int i = 0; i < 60; ++i) await Deposit(false, $"refute-{i}");
        var before = await FeedbackContent.ConsensusStateAsync(pg.DataSource, subject, relation, obj);
        Assert.NotNull(before);
        Assert.Equal(80, before.WitnessCount);
        var replay = await Deposit(false, "refute-59");
        Assert.Equal(0, replay.AttestationsInserted);
        Assert.Equal(0, replay.ConsensusUpdated);
        Assert.Equal(before, await FeedbackContent.ConsensusStateAsync(pg.DataSource, subject, relation, obj));

        async Task<FeedbackContent.DepositResult> Deposit(bool confirm, string occurrence)
        {
            var change = FeedbackContent.BuildTriple(subject, "RELATED_TO", obj, confirm, occurrence);
            try { return await FeedbackContent.ApplyAsync(pg.DataSource, change); }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }
}
