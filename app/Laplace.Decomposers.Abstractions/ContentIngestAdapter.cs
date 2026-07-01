using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public readonly record struct ContentIngestRecord(byte[] CanonicalUtf8, int Sequence = 0);

public sealed class ContentIngestHandler : IIngestRecordHandler<ContentIngestRecord>
{
    private readonly Hash128 _sourceId;

    public ContentIngestHandler(Hash128 sourceId) => _sourceId = sourceId;

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        ContentIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        new ContentDeferredUnit(record.CanonicalUtf8, _sourceId);

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
        }

        public TierTree? TreeForBatchProbe
        {
            get
            {
                if (_tree is null)
                    _tree = IntentStage.BuildContentTree(_canonical);
                return _tree;
            }
        }

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct)
        {
            if (TreeForBatchProbe is null)
                return Task.FromResult<byte[]?>(null);
            return TierTreeContainmentProbe.ProbeNodeEmitBitmapAsync(_tree!, reader, ct);
        }

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            if (_tree is null)
                _tree = IntentStage.BuildContentTree(_canonical);
            if (_tree is null) return default;
            builder.ContentStage.EmitContentTree(
                _tree, _sourceId, descentBitmap ?? ReadOnlySpan<byte>.Empty, out var rootId);
            return rootId;
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

    public FakeTabIngestDecomposer(
        IReadOnlyList<ContentIngestRecord> records,
        Hash128 sourceId,
        int batchSize = 4,
        ISubstrateReader? containmentReader = null)
    {
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
