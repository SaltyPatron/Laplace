using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Base for every relation-triple source (ATOMIC, ConceptNet, …). A subclass implements
/// ONLY <see cref="ExtractRecordsAsync"/> — pure content → (subject, relation, object)
/// records. All ingestion (perfcache tier-tree build, working-set descent dedup, bulk
/// COPY, Glicko fold) is the one shared pipeline: IngestBatchPipeline working-set mode
/// driving the single <see cref="RelationTripleHandler"/>. There is no per-source
/// ingestion code, no grammar-compose lane, and no hand-rolled builder loop.
/// </summary>
public abstract class RelationTripleDecomposerBase : IDecomposer
{
    public abstract Engine.Core.Hash128 SourceId { get; }
    public abstract string SourceName { get; }
    public abstract int LayerOrder { get; }
    public abstract Engine.Core.Hash128 TrustClassId { get; }

    /// <summary>Source trust weight carried into the fold (SourceTrust.* constant).</summary>
    protected abstract double SourceTrust { get; }

    public abstract Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The ONLY per-source code: stream generic triple records from the raw files.</summary>
    protected abstract IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct);

    protected ISubstrateReader? ContainmentReader { get; private set; }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainmentReader = context.Reader;
        if (options.DryRun) yield break;

        var stream = new AsyncEnumerableRecordStream<RelationTripleRecord>(
            ExtractRecordsAsync(context.EcosystemPath, options, ct));
        var handler = new RelationTripleHandler(SourceId, SourceTrust);
        var config = new IngestBatchConfig
        {
            SourceId = SourceId,
            BatchLabelPrefix = SourceName,
            BatchSize = options.BatchSize > 1 ? options.BatchSize : 16384,
            ContainmentReader = context.Reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
        };

        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }
}
