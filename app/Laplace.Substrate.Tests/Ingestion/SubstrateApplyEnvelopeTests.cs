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
    public void SourceReleaseFailureDoesNotAbandonTheOtherLeases()
    {
        var failure = new IOException("source close failed");
        var first = new TrackedLifetime(failure);
        var second = new TrackedLifetime();
        var changes = new[] { OwnedChange(first, 0), OwnedChange(second, 1) };
        try
        {
            Assert.Same(failure, Assert.Throws<IOException>(() => SubstrateApplyEnvelope.Release(changes)));
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
        }
        finally { SubstrateApplyEnvelope.Release(changes); }
    }

    [Fact]
    public async Task FailedApplyAndFailedSourceReleaseBothReachTheCallerAfterAllLeasesClose()
    {
        var applyFailure = new InvalidDataException("apply refused the source");
        var closeFailure = new IOException("source close failed");
        var first = new TrackedLifetime(closeFailure);
        var second = new TrackedLifetime();
        var decomposer = new OwnedChangesDecomposer([OwnedChange(first, 0), OwnedChange(second, 1)]);
        var runner = new IngestRunner(new FailedWriter(applyFailure, decomposer.Enumerated.Task),
            new EmptyReader(), NullLoggerFactory.Instance);
        var error = await Assert.ThrowsAsync<AggregateException>(() => runner.RunAsync(decomposer,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true, SkipSourceCompletion = true }));
        Assert.Contains(applyFailure, error.Flatten().InnerExceptions);
        Assert.Contains(closeFailure, error.Flatten().InnerExceptions);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task FailedCancellationCallbackDoesNotAbandonQueuedSourceLeasesOrHideTheApplyFailure()
    {
        var applyFailure = new InvalidDataException("apply refused the source");
        var cancelFailure = new IOException("producer cancellation callback failed");
        var first = new TrackedLifetime();
        var queued = new TrackedLifetime();
        var barrier = new IngestApplyBarrier();
        SubstrateChange boundary = new SubstrateChangeBuilder(
            Hash128.OfCanonical("test/cleanup/source"), "barrier").Build() with { ApplyBarrier = barrier };
        var decomposer = new OwnedChangesDecomposer(
            [OwnedChange(first, 0), boundary, OwnedChange(queued, 1)], cancelFailure);
        var runner = new IngestRunner(new FailedWriter(applyFailure, decomposer.Enumerated.Task),
            new EmptyReader(), NullLoggerFactory.Instance);
        var error = await Assert.ThrowsAsync<AggregateException>(() => runner.RunAsync(decomposer,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true, SkipSourceCompletion = true }));
        Assert.Contains(applyFailure, error.Flatten().InnerExceptions);
        Assert.Contains(cancelFailure, error.Flatten().InnerExceptions);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, queued.DisposeCount);
        Assert.Same(applyFailure, await Assert.ThrowsAsync<InvalidDataException>(() => barrier.WaitAsync()));
    }

    private static SubstrateChange OwnedChange(TrackedLifetime lifetime, int index) =>
        new SubstrateChangeBuilder(Hash128.OfCanonical("test/cleanup/source"), $"cleanup-{index}").Build()
            with { ApplyEnvelope = SubstrateApplyEnvelope.Own(lifetime, _ => ValueTask.CompletedTask) };

    [Fact]
    public async Task ProducerAndApplyFailuresBothSurviveWhileQueuedSourceLeasesClose()
    {
        var applyFailure = new InvalidDataException("apply refused the source");
        var producerFailure = new IOException("source enumeration failed");
        var first = new TrackedLifetime();
        var queued = new TrackedLifetime();
        var barrier = new IngestApplyBarrier();
        var boundary = new SubstrateChangeBuilder(
            Hash128.OfCanonical("test/cleanup/source"), "barrier").Build() with { ApplyBarrier = barrier };
        var decomposer = new OwnedChangesDecomposer(
            [OwnedChange(first, 0), boundary, OwnedChange(queued, 1)], terminalFailure: producerFailure);
        var runner = new IngestRunner(new FailedWriter(applyFailure, decomposer.Enumerated.Task),
            new EmptyReader(), NullLoggerFactory.Instance);

        var error = await Assert.ThrowsAsync<AggregateException>(() => runner.RunAsync(decomposer,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true, SkipSourceCompletion = true }));
        Assert.Contains(applyFailure, error.Flatten().InnerExceptions);
        Assert.Contains(producerFailure, error.Flatten().InnerExceptions);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, queued.DisposeCount);
        Assert.Same(applyFailure, await Assert.ThrowsAsync<InvalidDataException>(() => barrier.WaitAsync()));
    }

    [Fact]
    public async Task ApplyAndAbortFailuresAreBothRetained()
    {
        var applyFailure = new InvalidDataException("apply refused the source");
        var abortFailure = new IOException("bulk writer abort failed");
        var source = new TrackedLifetime();
        var decomposer = new OwnedChangesDecomposer([OwnedChange(source, 0)]);
        var runner = new IngestRunner(new FailedWriter(applyFailure, decomposer.Enumerated.Task, abortFailure),
            new EmptyReader(), NullLoggerFactory.Instance);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => runner.RunAsync(decomposer,
            IngestRunOptions.Default with { SkipLayerOrderingCheck = true, SkipSourceCompletion = true }));
        Assert.Contains(applyFailure, failure.Flatten().InnerExceptions);
        Assert.Contains(abortFailure, failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    private sealed class OwnedChangesDecomposer(SubstrateChange[] changes, Exception? cancelFailure = null,
        Exception? terminalFailure = null) : IDecomposer
    {
        internal TaskCompletionSource Enumerated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Hash128 SourceId => Hash128.OfCanonical("test/cleanup/source");
        public string SourceName => "CleanupOwnership";
        public int LayerOrder => 0;
        public Hash128 TrustClassId => TrustClassRegistry.Id("SubstrateMandate");
        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default) => Task.FromResult<long?>(changes.Length);
        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(IDecomposerContext context,
            DecomposerOptions options, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (cancelFailure is not null) ct.Register(() => throw cancelFailure);
            try
            {
                foreach (var change in changes) { ct.ThrowIfCancellationRequested(); yield return change; await Task.Yield(); }
                if (terminalFailure is not null) throw terminalFailure;
            }
            finally { Enumerated.TrySetResult(); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailedWriter(Exception failure, Task enumerated, Exception? abortFailure = null) : ISubstrateWriter
    {
        public async Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            await enumerated.WaitAsync(ct);
            throw failure;
        }
        public async Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes,
            Func<CancellationToken, ValueTask> precommitVerifier, CancellationToken ct = default)
        {
            await enumerated.WaitAsync(ct);
            await precommitVerifier(ct);
            throw failure;
        }
        public Task AbortBulkRunAsync(CancellationToken ct = default) => abortFailure is null
            ? Task.CompletedTask : Task.FromException(abortFailure);
    }

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
            TrustClassRegistry.Id("SubstrateMandate");

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

    private sealed class TrackedLifetime(Exception? failure = null) : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Verify()
        {
            if (DisposeCount != 0)
                throw new ObjectDisposedException(nameof(TrackedLifetime));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (failure is not null) throw failure;
        }
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
