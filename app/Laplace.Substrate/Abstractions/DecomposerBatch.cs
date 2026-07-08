using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Imperative-compose pipeline entry for non-orchestrator code (e.g. ModelTokenEdgeETL).
/// Multi-phase sources use nested <see cref="ComposeDecomposerPhase{T}"/> types instead.
/// </summary>
public static class IngestComposePipeline
{
    public static IAsyncEnumerable<SubstrateChange> RunAsync<T>(
        IAsyncEnumerable<T> records,
        Action<T, SubstrateChangeBuilder> compose,
        Hash128 sourceId,
        string labelPrefix,
        int batchSize,
        ISubstrateReader? reader,
        DecomposerOptions options,
        CancellationToken ct = default,
        int commitEpoch = 0,
        int? attestationCapacity = null)
    {
        if (options.DryRun) return Empty();

        int cap = Math.Max(1, batchSize);
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.Compose(
                sourceId, labelPrefix, cap, options, reader,
                attestationCapacity: attestationCapacity, commitEpoch: commitEpoch),
            options);
        return IngestBatchPipeline.RunAsync(
            new AsyncEnumerableRecordStream<T>(records),
            new DirectComposeHandler<T>(compose),
            config, ct);
    }

    private static async IAsyncEnumerable<SubstrateChange> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Handler for the imperative-compose model: no content-tree probe, the record's compose
/// callback runs in DrainInto against the shared working-set builder.
/// </summary>
public sealed class DirectComposeHandler<T> : IIngestRecordHandler<T>
{
    private readonly Action<T, SubstrateChangeBuilder> _compose;

    public DirectComposeHandler(Action<T, SubstrateChangeBuilder> compose) => _compose = compose;

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        T record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(T record) => new Unit(record, _compose);

    public void WalkWitness(T record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    private sealed class Unit(T record, Action<T, SubstrateChangeBuilder> compose) : IIngestDeferredUnit
    {
        public TierTree? TreeForBatchProbe => null;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            compose(record, builder);
            return default;
        }

        public void Dispose() { }
    }
}
