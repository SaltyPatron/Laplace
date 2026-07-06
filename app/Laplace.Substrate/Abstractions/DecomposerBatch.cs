using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Record → SubstrateChange lane for sources that compose imperatively into a builder
/// (iso, framenet, propbank, verbnet, wordnet). It is now a thin adapter over the ONE
/// ingestion pipeline: DecomposerBatch.RunAsync wraps the caller's compose callback in a
/// <see cref="DirectComposeHandler{T}"/> and delegates to IngestBatchPipeline.RunAsync
/// (working-set mode). It no longer carries its own batch loop, so the run bracket,
/// working-set budget valve, progress heartbeat, and any future produce-side change land
/// in exactly one place. These sources compose directly (no content-tree descent probe),
/// so the handler's unit reports no probe tree and just runs compose in DrainInto — the
/// apply's working-set subtraction dedups, exactly as before.
/// </summary>
public static class DecomposerBatch
{
    public static IAsyncEnumerable<SubstrateChange> RunAsync<T>(
        IAsyncEnumerable<T> records,
        Action<T, SubstrateChangeBuilder> compose,
        Hash128 sourceId,
        string labelPrefix,
        int batchSize,
        ISubstrateReader? reader,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        if (options.DryRun) return Empty();

        int cap = Math.Max(1, batchSize);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = labelPrefix,
            BatchSize = cap,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            // Preserve the prior builder sizing hints for this lane's row shape.
            EntityCapacity = cap * 4,
            PhysicalityCapacity = cap * 2,
            AttestationCapacity = cap * 8,
        };
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
/// callback runs in DrainInto against the shared working-set builder. Shared by
/// DecomposerBatch and available to any source that emits entities/attestations directly
/// rather than as a probeable content tree.
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
        // No probeable tree: this lane composes imperatively; dedup falls to the apply's
        // working-set subtraction, exactly as the old DecomposerBatch loop relied on.
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
