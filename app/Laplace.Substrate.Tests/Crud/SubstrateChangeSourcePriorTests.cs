using System.Collections.Immutable;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class SubstrateChangeSourcePriorTests
{
    private static readonly Hash128 Source = new(1, 2);
    private static readonly Hash128 OtherSource = new(3, 4);

    [Fact]
    public void MissingPriorRemainsExplicitUntilAnObservationIsWritten()
    {
        var change = new SubstrateChangeBuilder(Source, "no-prior").Build();
        Assert.Empty(change.PhysicalitySourcePriors);
        Assert.Throws<InvalidOperationException>(() => change.RequireSourcePrior(Source));
    }

    [Fact]
    public void DistinctSourcesKeepExactDeclaredPriorsAndImmutableSnapshots()
    {
        var builder = new SubstrateChangeBuilder(Source, "mixed")
            .DeclareSourcePrior(0.3).DeclareSourcePrior(OtherSource, 0.2);
        var entity = new Hash128(7, 8);
        var placement = PhysicalityId.Compute(entity, PhysicalityType.Projection);
        builder.AddPhysicality(new(placement, entity, Source, PhysicalityType.Projection,
            0.1, 0, 0, 0, default, null, 0, null, null, 1));
        builder.AddPhysicality(new(placement, entity, OtherSource, PhysicalityType.Projection,
            0.2, 0, 0, 0, default, null, 0, null, null, 2));
        var first = builder.Build();
        Assert.Single(first.Physicalities);
        Assert.Equal(2, first.PhysicalityObservations.Length);
        Assert.Equal(0.3, first.RequireSourcePrior(first.PhysicalityObservations[0].SourceId));
        Assert.Equal(0.2, first.RequireSourcePrior(first.PhysicalityObservations[1].SourceId));
        Assert.Equal(0.3, first.RequireSourcePrior(Source));
        Assert.Equal(0.2, first.RequireSourcePrior(OtherSource));
        builder.DeclareSourcePrior(new Hash128(5, 6), 0.95);
        Assert.Equal(2, first.PhysicalitySourcePriors.Count);
        Assert.Equal(3, builder.Build().PhysicalitySourcePriors.Count);
    }

    [Fact]
    public void RepeatedDeclarationIsIdempotentAndConflictingDeclarationFails()
    {
        var builder = new SubstrateChangeBuilder(Source, "exact").DeclareSourcePrior(0.7);
        builder.DeclareSourcePrior(0.7);
        Assert.Throws<InvalidOperationException>(() => builder.DeclareSourcePrior(0.85));
        var change = builder.Build();
        Assert.Equal(0.7, change.RequireSourcePrior(Source));
        Assert.Same(change, change.WithSourcePrior(Source, 0.7));
        Assert.Throws<InvalidOperationException>(() => change.WithSourcePrior(Source, 0.85));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidPriorCannotEnterOrEscapeThroughCallerSuppliedMetadata(double value)
    {
        var builder = new SubstrateChangeBuilder(Source, "invalid");
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.DeclareSourcePrior(value));
        var change = builder.Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => change.WithSourcePrior(Source, value));
        var supplied = change with { PhysicalitySourcePriors = ImmutableDictionary<Hash128, double>.Empty.Add(Source, value) };
        Assert.Throws<ArgumentOutOfRangeException>(() => supplied.RequireSourcePrior(Source));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void ExplicitEndpointPriorsArePreserved(double value)
    {
        var change = new SubstrateChangeBuilder(Source, "boundary").DeclareSourcePrior(value).Build();
        Assert.Equal(value, change.RequireSourcePrior(Source));
    }

    [Fact]
    public void PhaseDeclarationPreservesActualUnitAndPreviouslyDeclaredOtherSources()
    {
        var change = new SubstrateChangeBuilder(Source, "phase")
            .DeclareSourcePrior(OtherSource, 0.4).Build();
        var declared = change.WithSourcePrior(Source, 0.85);
        Assert.Same(change.Metadata, declared.Metadata);
        Assert.Equal(change.Metadata.IntentId, declared.Metadata.IntentId);
        Assert.Equal(change.PhysicalityObservations, declared.PhysicalityObservations);
        Assert.Equal(0.4, declared.RequireSourcePrior(OtherSource));
        Assert.Equal(0.85, declared.RequireSourcePrior(Source));
        Assert.False(change.PhysicalitySourcePriors.ContainsKey(Source));
    }

    [Fact]
    public void NativeContentEmissionsPreserveMixedSourcesInActualRowOrder()
    {
        CodepointPerfcache.LoadDefault();
        var builder = new SubstrateChangeBuilder(Source, "native-mixed")
            .DeclareSourcePrior(0.3).DeclareSourcePrior(OtherSource, 0.2);
        var stage = builder.ContentStage;
        Assert.True(stage.TryAddContentWitness(Encoding.UTF8.GetBytes("alpha"), Source, out var firstRoot));
        int firstCount = stage.PhysicalityCount;
        Assert.True(firstCount > 0);
        var firstSnapshot = stage.PhysicalitySourceRanges;
        Assert.True(stage.TryAddContentWitness(Encoding.UTF8.GetBytes("beta"), OtherSource, out var secondRoot));
        Assert.NotEqual(firstRoot, secondRoot);
        int secondCount = stage.PhysicalityCount - firstCount;
        Assert.True(secondCount > 0);
        var change = builder.Build();
        Assert.Same(stage, Assert.Single(change.IntentStages));
        Assert.Equal(new[] { new PhysicalitySourceRange(0, firstCount, Source),
            new PhysicalitySourceRange(firstCount, secondCount, OtherSource) },
            stage.PhysicalitySourceRanges.ToArray());
        Assert.Single(firstSnapshot);
        Assert.All(stage.PhysicalitySourceRanges,
            range => Assert.Equal(range.SourceId == Source ? 0.3 : 0.2,
                change.RequireSourcePrior(range.SourceId)));
        stage.Dispose();
    }

    [Fact]
    public void NativeSourceRangesRejectOverlapAndClaimsBeyondActualRows()
    {
        using var stage = IntentStage.New(2);
        var entity = new Hash128(7, 8);
        var placement = PhysicalityId.Compute(entity, PhysicalityType.Projection);
        stage.AddPhysicality(placement, entity, (short)PhysicalityType.Projection,
            new double[] { 0.1, 0, 0, 0 }, default, ReadOnlySpan<double>.Empty,
            0, null, null, 1);
        Assert.Empty(stage.PhysicalitySourceRanges);
        Assert.Throws<ArgumentOutOfRangeException>(() => stage.RecordPhysicalitySourceRange(0, 2, Source));
        stage.RecordPhysicalitySourceRange(0, 1, Source);
        Assert.Throws<InvalidOperationException>(() => stage.RecordPhysicalitySourceRange(0, 1, OtherSource));
        Assert.Equal(new PhysicalitySourceRange(0, 1, Source), Assert.Single(stage.PhysicalitySourceRanges));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.375)]
    [InlineData(1.0)]
    public void RawFileProducerDeclaresItsActualFileMetadataOwnerPrior(double prior)
    {
        CodepointPerfcache.LoadDefault();
        byte[] content = Encoding.UTF8.GetBytes("one unchanged physical file");
        var metadata = new FileMetadata("prior.txt", "proof/prior.txt", content.Length,
            DateTime.UnixEpoch, "text");
        var record = new GrammarComposeRecord(content, "text", FileMetadata: metadata, RawText: true);
        var handler = new GrammarComposeHandler(Source, prior, null);
        var builder = new SubstrateChangeBuilder(Source, "raw-file-prior");
        using var unit = handler.CreateDeferredUnit(record);
        // The relation weight is deliberately different from the source prior.
        Hash128 file = unit.DrainInto(builder, 0.17, null);
        SubstrateChange change = builder.Build();
        try
        {
            Assert.Equal(FileEntity.Resolve(content, metadata).FileId, file);
            Assert.Equal(file, change.Metadata.FileId);
            Assert.Equal(prior, change.RequireSourcePrior(Source));
            Assert.Equal(prior, change.RequireSourcePrior(file));
            var ranges = change.IntentStages.SelectMany(stage => stage.PhysicalitySourceRanges).ToArray();
            Assert.Contains(ranges, range => range.SourceId == file);
            Assert.All(ranges, range => Assert.Equal(prior, change.RequireSourcePrior(range.SourceId)));
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Fact]
    public void UserTextArtifactDeclaresTenantContentAndFileObservationPriors()
    {
        CodepointPerfcache.LoadDefault();
        var scope = UserArtifactContent.Resolve("prior-proof", tenantTrust: 0.35);
        double prior = SourceTrust.UserPrompt * scope.TenantTrust;
        Assert.True(UserArtifactContent.TryBuildTextArtifactChange(scope, "note.txt", "proof/note.txt",
            Encoding.UTF8.GetBytes("reused content still has its observed form"), null,
            DateTime.UnixEpoch, out var change, out var ids));
        try
        {
            Assert.Equal(prior, change.RequireSourcePrior(scope.Source));
            Assert.Equal(prior, change.RequireSourcePrior(ids.ContentId));
            Assert.Equal(prior, change.RequireSourcePrior(ids.FileId));
            var ranges = change.IntentStages.SelectMany(stage => stage.PhysicalitySourceRanges).ToArray();
            Assert.Contains(ranges, range => range.SourceId == ids.ContentId);
            Assert.Contains(ranges, range => range.SourceId == ids.FileId);
            Assert.All(ranges, range => Assert.Equal(prior, change.RequireSourcePrior(range.SourceId)));
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Fact]
    public void LegacyFileMetadataRetainsItsExplicitMandatePrior()
    {
        CodepointPerfcache.LoadDefault();
        var builder = new SubstrateChangeBuilder(Source, "legacy-file-metadata");
        FileEntity.EmitMetadata(builder, OtherSource,
            new FileMetadata("image.png", "proof/image.png", 16, DateTime.UnixEpoch));
        SubstrateChange change = builder.Build();
        try
        {
            var ranges = change.IntentStages.SelectMany(stage => stage.PhysicalitySourceRanges).ToArray();
            Assert.NotEmpty(ranges);
            Assert.All(ranges, range =>
            {
                Assert.Equal(OtherSource, range.SourceId);
                Assert.Equal(SourceTrust.SubstrateMandate, change.RequireSourcePrior(range.SourceId));
            });
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Fact]
    public void ExplicitSingleSourceAttachmentDoesNotInventOwnersForUnannotatedStages()
    {
        using var stage = IntentStage.New(1);
        var entity = new Hash128(7, 8);
        stage.AddPhysicality(PhysicalityId.Compute(entity, PhysicalityType.Projection), entity,
            (short)PhysicalityType.Projection, new double[] { 0.1, 0, 0, 0 }, default,
            ReadOnlySpan<double>.Empty, 0, null, null, 1);
        var undeclared = new SubstrateChangeBuilder(Source, "no-stage-owner").AddIntentStage(stage).Build();
        Assert.Empty(Assert.Single(undeclared.IntentStages).PhysicalitySourceRanges);
        var declared = new SubstrateChangeBuilder(Source, "actual-stage-owner")
            .DeclareSourcePrior(OtherSource, 0.2).AddIntentStage(stage, OtherSource).Build();
        var range = Assert.Single(Assert.Single(declared.IntentStages).PhysicalitySourceRanges);
        Assert.Equal(OtherSource, range.SourceId);
        Assert.Equal(0.2, declared.RequireSourcePrior(range.SourceId));
    }

    [Fact]
    public void OrderedNativeBatchPreservesSourceRangesForEveryRawFormAcrossCollapseAndReplay()
    {
        CodepointPerfcache.LoadDefault();
        static OrderedCompositionComponent Atom(char value)
        {
            var p = CodepointPerfcache.Records[value];
            return new(p.Hash, 0, p.CoordX, p.CoordY, p.CoordZ, p.CoordM, value, true);
        }
        using var stage = IntentStage.New(4);
        var a = Atom('A');
        var b = Atom('B');
        var type = new Hash128(9, 10);
        OrderedCompositionRequest[] requests =
        [
            new([a, b], type, Source, 1),
            new([a], type, OtherSource, 2),
            new([a, b], type, OtherSource, 3),
            new([b, a], type, OtherSource, 4),
        ];
        var results = new OrderedCompositionResult[requests.Length];
        OrderedComposition.StageBatch(stage, requests, results);
        Assert.Equal(results[0].Id, results[2].Id);
        Assert.Equal(a.Id, results[1].Id);
        Assert.NotEqual(results[0].Id, results[3].Id);
        Assert.Equal(2, stage.EntityCount);
        Assert.Equal(4, stage.PhysicalityCount);
        Assert.Equal(new[] { new PhysicalitySourceRange(0, 1, Source),
            new PhysicalitySourceRange(1, 3, OtherSource) }, stage.PhysicalitySourceRanges.ToArray());
        var before = stage.PhysicalitySourceRanges;
        OrderedComposition.StageBatch(stage, requests, results);
        Assert.Equal(2, stage.EntityCount);
        Assert.Equal(8, stage.PhysicalityCount);
        Assert.Equal(new[] { new PhysicalitySourceRange(0, 1, Source),
            new PhysicalitySourceRange(1, 4, OtherSource),
            new PhysicalitySourceRange(5, 1, Source),
            new PhysicalitySourceRange(6, 2, OtherSource) }, stage.PhysicalitySourceRanges.ToArray());
        Assert.Equal(2, before.Length);
    }
}
