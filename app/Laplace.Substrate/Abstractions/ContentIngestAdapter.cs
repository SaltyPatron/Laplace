using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

// Pillar 0: SourceId is the per-file FILE-ENTITY provenance (content DAG ⊕ metadata DAG, see
// FileEntity) when the producing stream computed it; default(Hash128) means "fall back to the
// decomposer's static config source" for streams not yet converted to per-file provenance.
public readonly record struct ContentIngestRecord(byte[] CanonicalUtf8, int Sequence = 0, Hash128 SourceId = default);

public sealed class ContentIngestHandler : IIngestRecordHandler<ContentIngestRecord>
{
    private readonly Hash128 _sourceId;

    public ContentIngestHandler(Hash128 sourceId) => _sourceId = sourceId;

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        ContentIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        new ContentDeferredUnit(record.CanonicalUtf8,
            record.SourceId.Equals(default(Hash128)) ? _sourceId : record.SourceId);

    public void WalkWitness(ContentIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
    }

    private sealed class ContentDeferredUnit : IIngestDeferredUnit
    {
        private readonly byte[] _canonical;
        private readonly Hash128 _sourceId;
        private TierTree? _tree;
        private bool _disposed;

        public ContentDeferredUnit(byte[] canonical, Hash128 sourceId)
        {
            _canonical = canonical;
            _sourceId = sourceId;
            // The heavy content-tier-tree build happens HERE, at construction — and
            // CreateDeferredUnit is invoked on the pinned parallel workers inside
            // IngestDescentFlush.ComposeBatchAsync. content_tree_build is lock-free, per-call,
            // and reads the perfcache read-only, so the decompose fans out across cores. Building
            // it lazily (below) instead ran every file one-at-a-time in the sequential
            // FinalizePendingAsync loop — the single-core "compose" bottleneck.
            _tree = ContentTierSpine.BuildTree(canonical);
        }

        // Fallback only: the tree is normally already built by the constructor on a compose
        // worker. This keeps the probe correct if a unit is ever created off that path.
        public TierTree? TreeForBatchProbe => _tree ??= ContentTierSpine.BuildTree(_canonical);

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _tree is null
                ? Task.FromResult<byte[]?>(null)
                : ContentTierSpine.ExistenceEmitBitmapAsync(_tree, reader, ct);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            if (_tree is null)
                _tree = ContentTierSpine.BuildTree(_canonical);
            if (_tree is null) return default;
            return ContentTierSpine.EmitTree(
                builder, _tree, _sourceId, descentBitmap ?? ReadOnlySpan<byte>.Empty, out var rootId)
                ? rootId : default;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tree?.Dispose();
            _tree = null;
        }
    }
}

public sealed class FakeTabIngestDecomposer : IDecomposer
{
    private readonly IReadOnlyList<ContentIngestRecord> _records;
    private readonly IngestBatchConfig _config;
    private readonly bool _workingSet;

    public FakeTabIngestDecomposer(
        IReadOnlyList<ContentIngestRecord> records,
        Hash128 sourceId,
        int batchSize = 4,
        ISubstrateReader? containmentReader = null,
        bool? workingSet = null)
    {
        _workingSet = workingSet ?? WorkingSetMode.Enabled;
        _records = records;
        _config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = "fake-tab",
            BatchSize = batchSize,
            ContainmentReader = containmentReader,
        };
    }

    public Hash128 SourceId => _config.SourceId;
    public string SourceName => "FakeTab";
    public int LayerOrder => 99;
    public Hash128 TrustClassId => Hash128.OfCanonical("substrate/trust/test/fake-tab/v1");

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.DryRun) yield break;

        var stream = new FakeRecordStream(_records);
        var handler = new ContentIngestHandler(_config.SourceId);
        var config = new IngestBatchConfig
        {
            SourceId = _config.SourceId,
            BatchLabelPrefix = _config.BatchLabelPrefix,
            BatchSize = _config.BatchSize,
            ProbeChunkSize = _config.ProbeChunkSize,
            WitnessWeight = _config.WitnessWeight,
            CommitEpoch = _config.CommitEpoch,
            ContainmentReader = _config.ContainmentReader,
            ReportUnits = _config.ReportUnits,
            MaxInputUnits = _config.MaxInputUnits,
            WorkingSet = _workingSet,
        };

        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(_records.Count);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FakeRecordStream(IReadOnlyList<ContentIngestRecord> records) : IRecordStream<ContentIngestRecord>
    {
        public async IAsyncEnumerable<ContentIngestRecord> RecordsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return records[i];
                await Task.Yield();
            }
        }
    }
}
