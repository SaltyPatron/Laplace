using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One canonical content payload plus optional structural provenance.
///
/// <para><see cref="ContentRootId"/> is the globally shared content identity used for
/// existence/dedup. <see cref="SourceId"/> is who directly contains/witnesses that content
/// (for a document lane, the document entity). They are deliberately different concepts.</para>
///
/// <para><see cref="DocumentId"/> and <see cref="FileId"/> are the higher trunks when the
/// producer is a standard file/document lane. Other content lanes leave them zero.</para>
/// </summary>
public readonly record struct ContentIngestRecord(
    byte[] CanonicalUtf8,
    int Sequence = 0,
    Hash128 SourceId = default,
    FileMetadata? Metadata = null,
    Hash128 ContentRootId = default,
    Hash128 DocumentId = default,
    Hash128 FileId = default);

public sealed class ContentIngestHandler : IIngestRecordHandler<ContentIngestRecord>
{
    private readonly Hash128 _sourceId;

    public ContentIngestHandler(Hash128 sourceId) => _sourceId = sourceId;

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
            _tree = ContentTierSpine.BuildTree(canonical);
        }

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

public sealed class FakeTabIngestDecomposer : Decomposer<ContentIngestRecord>
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

    public override Hash128 SourceId => _config.SourceId;
    public override string SourceName => "FakeTab";
    public override int LayerOrder => 99;
    public override Hash128 TrustClassId => SubstrateCanonicalIds.OfVersioned("trust", "test", "fake-tab");
    protected override double SourceTrust => 1.0;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    protected override IIngestRecordHandler<ContentIngestRecord> CreateHandler() =>
        new ContentIngestHandler(_config.SourceId);

    protected override async IAsyncEnumerable<ContentIngestRecord> ExtractRecordsAsync(
        string ecosystemPath,
        DecomposerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var record in _records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        new()
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

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(_records.Count);
}
