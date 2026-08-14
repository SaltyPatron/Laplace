using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One planar RGBA recovery buffer (tightly packed, row-major) plus optional
/// precomputed ladder root in <see cref="SourceId"/> (file-entity provenance,
/// same convention as <see cref="ContentIngestRecord"/>). Buffer is packaging
/// output; identity is the codepoint-floor image ladder.
/// </summary>
public readonly record struct ImageIngestRecord(
    byte[] Rgba,
    uint Width,
    uint Height,
    Hash128 SourceId = default,
    FileMetadata? Metadata = null);

/// <summary>
/// One mono int16 recovery stream for the audio ladder (channel is a partition,
/// not a tier). Packaging decode lands here; identity is the codepoint-floor
/// audio ladder, not blake3 of PCM.
/// </summary>
public readonly record struct AudioIngestRecord(
    short[] Pcm,
    int SampleRate,
    Hash128 SourceId = default,
    FileMetadata? Metadata = null);

/// <summary>
/// One video frame — image-ladder recovery payload plus temporal ordinal.
/// </summary>
public readonly record struct VideoFrameIngestRecord(
    byte[] Rgba,
    uint Width,
    uint Height,
    int FrameIndex,
    Hash128 SourceId = default,
    FileMetadata? Metadata = null);

public sealed class ImageIngestHandler : IIngestRecordHandler<ImageIngestRecord>
{
    private readonly Hash128 _sourceId;
    private readonly int _layerOrder;
    public bool IgnoreCompletedFiles { get; init; }

    public ImageIngestHandler(Hash128 sourceId, int layerOrder)
    {
        _sourceId = sourceId;
        _layerOrder = layerOrder;
    }

    public IIngestDeferredUnit CreateDeferredUnit(ImageIngestRecord record) =>
        new ImageDeferredUnit(record,
            record.SourceId.Equals(default(Hash128)) ? _sourceId : record.SourceId);

    public void WalkWitness(ImageIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (unit is PresentRootDeferredUnit) return;
        Hash128 fileRoot = record.SourceId != default ? record.SourceId
            : root != default ? root
            : ImageTierSpine.ResolveRoot(record.Rgba, record.Width, record.Height) ?? default;
        if (fileRoot == default) return;
        Laplace.Ingestion.LayerCompletion.EmitFileMarker(builder, fileRoot, _layerOrder);
        if (record.Metadata is { } metadata)
            FileEntity.EmitMetadata(builder, fileRoot, metadata);
    }

    private sealed class ImageDeferredUnit : IIngestDeferredUnit
    {
        private readonly ImageIngestRecord _record;
        private readonly Hash128 _sourceId;
        private TierTree? _tree;
        private bool _disposed;

        public ImageDeferredUnit(ImageIngestRecord record, Hash128 sourceId)
        {
            _record = record;
            _sourceId = sourceId;
            _tree = ImageTierSpine.BuildTree(record.Rgba, record.Width, record.Height);
        }

        public TierTree? TreeForBatchProbe => _tree ??= ImageTierSpine.BuildTree(
            _record.Rgba, _record.Width, _record.Height);

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _tree is null
                ? Task.FromResult<byte[]?>(null)
                : ImageTierSpine.ExistenceEmitBitmapAsync(_tree, reader, ct);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            if (_tree is null)
                _tree = ImageTierSpine.BuildTree(_record.Rgba, _record.Width, _record.Height);
            if (_tree is null) return default;
            return ImageTierSpine.EmitTree(
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

public sealed class AudioIngestHandler : IIngestRecordHandler<AudioIngestRecord>
{
    private readonly Hash128 _sourceId;
    private readonly int _layerOrder;
    public bool IgnoreCompletedFiles { get; init; }

    public AudioIngestHandler(Hash128 sourceId, int layerOrder)
    {
        _sourceId = sourceId;
        _layerOrder = layerOrder;
    }

    public IIngestDeferredUnit CreateDeferredUnit(AudioIngestRecord record) =>
        new AudioDeferredUnit(record,
            record.SourceId.Equals(default(Hash128)) ? _sourceId : record.SourceId);

    public void WalkWitness(AudioIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (unit is PresentRootDeferredUnit) return;
        Hash128 fileRoot = record.SourceId != default ? record.SourceId
            : root != default ? root
            : AudioTierSpine.ResolveRoot(record.Pcm) ?? default;
        if (fileRoot == default) return;
        Laplace.Ingestion.LayerCompletion.EmitFileMarker(builder, fileRoot, _layerOrder);
        if (record.Metadata is { } metadata)
            FileEntity.EmitMetadata(builder, fileRoot, metadata);
    }

    private sealed class AudioDeferredUnit : IIngestDeferredUnit
    {
        private readonly AudioIngestRecord _record;
        private readonly Hash128 _sourceId;
        private TierTree? _tree;
        private bool _disposed;

        public AudioDeferredUnit(AudioIngestRecord record, Hash128 sourceId)
        {
            _record = record;
            _sourceId = sourceId;
            _tree = AudioTierSpine.BuildTree(record.Pcm);
        }

        public TierTree? TreeForBatchProbe => _tree ??= AudioTierSpine.BuildTree(_record.Pcm);

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _tree is null
                ? Task.FromResult<byte[]?>(null)
                : AudioTierSpine.ExistenceEmitBitmapAsync(_tree, reader, ct);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            if (_tree is null)
                _tree = AudioTierSpine.BuildTree(_record.Pcm);
            if (_tree is null) return default;
            return AudioTierSpine.EmitTree(
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

/// <summary>
/// Video frames share the image ladder; temporal membership edges are emitted in
/// <see cref="WalkWitness"/> (HAS_FRAME / PRECEDES_IN_TIME) once a frame root exists.
/// Frame-index map is concurrency-safe so drain order need not match extract order.
/// </summary>
public sealed class VideoFrameIngestHandler : IIngestRecordHandler<VideoFrameIngestRecord>
{
    // Declaration roster (the architecture-gate pattern): relation names are
    // spelled HERE once, resolved once, and emit sites below use resolved ids
    // (CategoricalResolved) — no ad-hoc call-site literals (ISA G3-C#).
    // Index coupling is deliberate and local: two entries, resolved on the
    // next two lines.
    private static readonly IReadOnlyList<string> DeclaredRelations =
        ["HAS_FRAME", "PRECEDES_IN_TIME"];
    private static readonly Hash128 HasFrameId =
        RelationTypeRegistry.RelationTypeId(DeclaredRelations[0]);
    private static readonly Hash128 PrecedesInTimeId =
        RelationTypeRegistry.RelationTypeId(DeclaredRelations[1]);

    private readonly Hash128 _sourceId;
    private readonly int _layerOrder;
    private readonly Hash128 _videoRootId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Hash128> _frameRoots = new();
    public bool IgnoreCompletedFiles { get; init; }

    public VideoFrameIngestHandler(Hash128 sourceId, int layerOrder, Hash128 videoRootId)
    {
        _sourceId = sourceId;
        _layerOrder = layerOrder;
        _videoRootId = videoRootId;
    }

    public IIngestDeferredUnit CreateDeferredUnit(VideoFrameIngestRecord record) =>
        new ImageIngestHandler(_sourceId, _layerOrder).CreateDeferredUnit(
            new ImageIngestRecord(record.Rgba, record.Width, record.Height, record.SourceId, record.Metadata));

    public void WalkWitness(VideoFrameIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (unit is PresentRootDeferredUnit) return;
        Hash128 frameRoot = root != default ? root
            : ImageTierSpine.ResolveRoot(record.Rgba, record.Width, record.Height) ?? default;
        if (frameRoot == default) return;

        // Deposit the video container entity once (content-addressed root over frame ids).
        builder.AddEntity(new EntityRow(
            _videoRootId, 4, EntityTypeRegistry.Video, _sourceId));
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            _videoRootId, HasFrameId, frameRoot, _sourceId, null, SourceTrust.StructuredCorpus));
        _frameRoots[record.FrameIndex] = frameRoot;
        if (_frameRoots.TryGetValue(record.FrameIndex - 1, out var prev))
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                prev, PrecedesInTimeId, frameRoot, _sourceId, null, SourceTrust.StructuredCorpus));
        if (_frameRoots.TryGetValue(record.FrameIndex + 1, out var next))
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                frameRoot, PrecedesInTimeId, next, _sourceId, null, SourceTrust.StructuredCorpus));

        Laplace.Ingestion.LayerCompletion.EmitFileMarker(builder, frameRoot, _layerOrder);
        if (record.Metadata is { } metadata)
            FileEntity.EmitMetadata(builder, frameRoot, metadata);
    }
}

public static class MediaIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, double witnessWeight, string batchLabelPrefix,
        ISubstrateReader? reader, IngestSourceProfile profile, int batchSize = 16)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSize);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = Math.Clamp(ws.Batch, 1, 256),
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 16, 256),
            WitnessWeight = witnessWeight,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
