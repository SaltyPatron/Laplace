using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Ingestion.Tests;

[Collection("GrammarPerfcache")]
public sealed class SubstrateApplyEnvelopeTests
{
    [Fact]
    public async Task ProducerCompletion_DoesNotReleaseSourceBeforeTransactionalVerification()
    {
        var lifetime = new TrackedLifetime();
        var enumerated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var decomposer = new EnvelopeDecomposer(lifetime, enumerated);
        var writer = new EnvelopeWriter(enumerated.Task, lifetime);
        var runner = new IngestRunner(
            writer, new EmptyReader(), NullLoggerFactory.Instance);

        IngestRunResult result = await runner.RunAsync(
            decomposer,
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });

        Assert.Equal(2, result.UnitsApplied);
        Assert.Equal(2, writer.AppliedChanges);
        Assert.Equal(1, writer.Verifications);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.True(writer.VerifiedAfterProducerCompleted);
    }

    [Fact]
    public async Task CancelledApply_ReleasesTransferredSourceLeaseExactlyOnce()
    {
        var lifetime = new TrackedLifetime();
        var enumerated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredApply = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var decomposer = new EnvelopeDecomposer(lifetime, enumerated);
        var writer = new EnvelopeWriter(
            enumerated.Task, lifetime, enteredApply, blockUntilCancelled: true);
        var runner = new IngestRunner(
            writer, new EmptyReader(), NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();

        Task run = runner.RunAsync(
            decomposer,
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            },
            cts.Token);
        await enteredApply.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task TransientRetry_KeepsSourceLeaseUntilSuccessfulVerification()
    {
        var lifetime = new TrackedLifetime();
        var enumerated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new EnvelopeWriter(
            enumerated.Task, lifetime, failFirstAttempt: true);
        var runner = new IngestRunner(
            writer, new EmptyReader(), NullLoggerFactory.Instance);

        IngestRunResult result = await runner.RunAsync(
            new EnvelopeDecomposer(lifetime, enumerated),
            IngestRunOptions.Default with
            {
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });

        Assert.Equal(2, result.UnitsApplied);
        Assert.Equal(2, writer.ApplyAttempts);
        Assert.Equal(1, writer.Verifications);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    private sealed class EnvelopeDecomposer(
        TrackedLifetime lifetime,
        TaskCompletionSource enumerated) : IDecomposer
    {
        public Hash128 SourceId { get; } = Hash128.OfCanonical("test/apply-envelope/source");
        public string SourceName => "ApplyEnvelope";
        public int LayerOrder => 0;
        public Hash128 TrustClassId =>
            SubstrateCanonicalIds.TrustClass("SubstrateMandate");

        public Task InitializeAsync(
            IDecomposerContext context, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using SubstrateApplyEnvelope owner = SubstrateApplyEnvelope.Own(
                lifetime,
                verifyCt =>
                {
                    verifyCt.ThrowIfCancellationRequested();
                    lifetime.Verify();
                    return ValueTask.CompletedTask;
                });
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    yield return new SubstrateChangeBuilder(SourceId, $"held-source-{i}")
                        .SetInputUnitsConsumed(1)
                        .Build() with
                        {
                            ApplyEnvelope = owner.Retain(),
                        };
                    await Task.Yield();
                }
            }
            finally
            {
                enumerated.TrySetResult();
            }
        }

        public Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default) =>
            Task.FromResult<long?>(2);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EnvelopeWriter(
        Task producerCompleted,
        TrackedLifetime lifetime,
        TaskCompletionSource? enteredApply = null,
        bool blockUntilCancelled = false,
        bool failFirstAttempt = false) : ISubstrateWriter
    {
        public int Verifications { get; private set; }
        public int AppliedChanges { get; private set; }
        public int ApplyAttempts { get; private set; }
        public bool VerifiedAfterProducerCompleted { get; private set; }

        public Task<ApplyResult> ApplyAsync(
            SubstrateChange change, CancellationToken ct = default) =>
            Task.FromResult(EmptyResult());

        public async Task<ApplyResult> ApplyWorkingSetAsync(
            IReadOnlyList<SubstrateChange> changes,
            Func<CancellationToken, ValueTask> precommitVerifier,
            CancellationToken ct = default)
        {
            enteredApply?.TrySetResult();
            await producerCompleted.WaitAsync(ct);
            ApplyAttempts++;
            if (failFirstAttempt && ApplyAttempts == 1)
                throw new TimeoutException("injected transient apply failure");
            if (blockUntilCancelled)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            Assert.Equal(0, lifetime.DisposeCount);
            await precommitVerifier(ct);
            Verifications++;
            AppliedChanges += changes.Count;
            VerifiedAfterProducerCompleted = producerCompleted.IsCompletedSuccessfully;
            return EmptyResult();
        }

        private static ApplyResult EmptyResult() => new(
            0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false);
    }

    private sealed class TrackedLifetime : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Verify()
        {
            if (DisposeCount != 0)
                throw new ObjectDisposedException(nameof(TrackedLifetime));
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class EmptyReader : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(
            int layerOrder, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(
            Hash128 typeId, CancellationToken ct = default) => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default) =>
            Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
