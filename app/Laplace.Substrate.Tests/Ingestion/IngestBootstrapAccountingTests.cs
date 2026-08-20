using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class IngestBootstrapAccountingTests
{
    [Fact]
    public async Task InitializeWrites_AreIncludedInRunRowsAndAdmission()
    {
        var writer = new InsertAllWriter();
        var runner = new IngestRunner(
            writer, new EmptyReader(), NullLoggerFactory.Instance);

        var result = await runner.RunAsync(
            new BootstrapThenContentDecomposer(),
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });

        Assert.Equal(2, result.EntitiesInserted);
        Assert.Equal(1, result.PhysicalitiesInserted);
        Assert.Equal(1, result.AttestationsInserted);
        Assert.Equal(1, result.BootstrapEntitiesInserted);
        Assert.Equal(0, result.BootstrapPhysicalitiesInserted);
        Assert.Equal(0, result.BootstrapAttestationsInserted);
        Assert.Equal(1, result.GovernedIdentitiesWithoutPhysicality);
        Assert.Equal(1, result.InputUnitsDone);
        Assert.Equal(1, result.ConsensusObservations);
        Assert.Equal(1, result.ConsensusCellDeposits);
        Assert.Equal(1, result.BootstrapRowsInserted);
        Assert.Equal(3, result.PayloadRowsInserted);
        Assert.Equal(3, result.PayloadRowsPerInput);
        Assert.Equal(1, result.ObservationsPerCellDeposit);
    }

    [Fact]
    public async Task BypassCompletionGuard_StillWritesTerminalMarker()
    {
        var writer = new InsertAllWriter();
        var runner = new IngestRunner(
            writer, new EmptyReader(sourceCompleted: true), NullLoggerFactory.Instance);

        var result = await runner.RunAsync(
            new BootstrapThenContentDecomposer(),
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                BypassSourceCompletionGuard = true,
            });

        Assert.Equal(1, result.UnitsApplied);
        Assert.Contains("layer-complete/0", writer.AppliedUnits);
    }

    private sealed class BootstrapThenContentDecomposer : IDecomposer
    {
        private static readonly Hash128 Source = Hash128.OfCanonical("test/bootstrap-accounting/source");
        private static readonly Hash128 Governed = Hash128.OfCanonical("test/bootstrap-accounting/governed");
        private static readonly Hash128 Content = Hash128.OfCanonical("test/bootstrap-accounting/content");

        public Hash128 SourceId => Source;
        public string SourceName => "BootstrapAccounting";
        public int LayerOrder => 0;
        public Hash128 TrustClassId => SubstrateCanonicalIds.TrustClass("SubstrateMandate");

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            var bootstrap = new SubstrateChangeBuilder(Source, "bootstrap/test")
                .AddEntity(Governed, EntityTier.Word, EntityTypeRegistry.SourceReference, Source)
                .Build();
            await context.Writer.ApplyAsync(bootstrap, ct);
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            double[] coord = [1, 0, 0, 0];
            var change = new SubstrateChangeBuilder(Source, "content/test")
                .AddEntity(Content, EntityTier.Word, EntityTypeRegistry.Word, Source)
                .AddPhysicality(new PhysicalityRow(
                    PhysicalityId.Compute(Content, PhysicalityType.Content),
                    Content, Source, PhysicalityType.Content,
                    1, 0, 0, 0, Hilbert128.Encode(coord), null, 0, null, null, 0))
                .SetInputUnitsConsumed(1);
            change.AddAttestation(NativeAttestation.CategoricalResolved(
                Content,
                RelationTypeRegistry.RelationTypeId("IS_TYPED_AS"),
                Governed,
                Source,
                contextId: null,
                witnessWeight: 1));
            var built = change.Build();
            yield return built;
            await Task.CompletedTask;
        }

        public Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default) => Task.FromResult<long?>(1);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InsertAllWriter : ISubstrateWriter, IConsensusFoldMetrics
    {
        public List<string> AppliedUnits { get; } = [];
        public long ObservationsAccumulated { get; private set; } = 11;
        public long CellsFolded { get; private set; } = 7;

        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            AppliedUnits.Add(change.Metadata.SourceContentUnitName);
            int entities = change.Entities.Length;
            int physicalities = change.Physicalities.Length;
            int attestations = change.Attestations.Length;
            if (!change.IntentStages.IsDefaultOrEmpty)
                foreach (var stage in change.IntentStages)
                {
                    entities += stage.EntityCount;
                    physicalities += stage.PhysicalityCount;
                    attestations += stage.AttestationCount;
                }
            ObservationsAccumulated += attestations;
            CellsFolded += attestations;
            return Task.FromResult(new ApplyResult(
                entities, entities,
                physicalities, physicalities,
                attestations, attestations,
                RoundTrips: 1,
                WallClock: TimeSpan.Zero,
                TrunkShortcircuitHit: false));
        }
    }

    private sealed class EmptyReader(bool sourceCompleted = false) : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(sourceCompleted);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) =>
            Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default) =>
            Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
