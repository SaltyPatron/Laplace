using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class DecomposerMultiPhaseBudgetTests
{
    [Fact]
    public async Task MaxInputUnits_IsSharedAcrossPhases()
    {
        var first = new SyntheticPhase(3);
        var second = new SyntheticPhase(3);
        var decomposer = new SyntheticMultiPhase(first, second);
        var options = DecomposerOptions.Default with { MaxInputUnits = 4 };

        var changes = new List<SubstrateChange>();
        await foreach (var change in decomposer.DecomposeAsync(new Context(), options))
            changes.Add(change);

        Assert.Equal(4, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.Equal(4, first.ReceivedCap);
        Assert.Equal(1, second.ReceivedCap);
    }

    private sealed class SyntheticMultiPhase(params SyntheticPhase[] phases) : DecomposerMultiPhase
    {
        public override Hash128 SourceId { get; } = Hash128.OfCanonical("test/multiphase");
        public override string SourceName => "test-multiphase";
        public override int LayerOrder => 1;
        public override Hash128 TrustClassId => default;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
            Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default) =>
            Task.FromResult<long?>(phases.Sum(p => p.Count));

        protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var phase in phases)
                await foreach (var change in RunPhaseAsync(phase, context, options, ct))
                    yield return change;
        }
    }

    private sealed class SyntheticPhase(int count) : IDecomposer
    {
        public int Count => count;
        public long ReceivedCap { get; private set; }
        public Hash128 SourceId { get; } = Hash128.OfCanonical($"test/phase/{count}");
        public string SourceName => "test-phase";
        public int LayerOrder => 1;
        public Hash128 TrustClassId => default;

        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReceivedCap = options.MaxInputUnits;
            int emit = options.MaxInputUnits > 0
                ? (int)Math.Min(count, options.MaxInputUnits)
                : count;
            for (int i = 0; i < emit; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new SubstrateChangeBuilder(SourceId, $"phase/{i}")
                    .SetInputUnitsConsumed(1)
                    .Build();
                await Task.Yield();
            }
        }

        public Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default) =>
            Task.FromResult<long?>(count);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Context : IDecomposerContext
    {
        public string EcosystemPath => string.Empty;
        public ISubstrateWriter Writer { get; } = new WriterStub();
        public ISubstrateReader Reader { get; } = new ReaderStub();
        public ILogger Logger => NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private sealed class WriterStub : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default) =>
            Task.FromResult(new ApplyResult(
                0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, TrunkShortcircuitHit: false));
    }

    private sealed class ReaderStub : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) =>
            Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default) =>
            Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
