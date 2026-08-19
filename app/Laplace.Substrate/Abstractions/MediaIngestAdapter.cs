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

/// <summary>
/// Video vendor records share the generic ingest pipeline. Frames compose normally;
/// the terminal record materializes their one ordered container after every frame
/// witness has drained.
/// </summary>
public abstract record VideoIngestRecord
{
    public sealed record Frame(VideoFrameIngestRecord Value) : VideoIngestRecord;
    public sealed record SequenceEnd : VideoIngestRecord;
}

internal interface IResolvedRootCoordinateUnit
{
    bool TryGetRootCoordinate(out double x, out double y, out double z, out double m);
}

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

    private sealed class ImageDeferredUnit : IIngestDeferredUnit, IResolvedRootCoordinateUnit
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

        public bool TryGetRootCoordinate(out double x, out double y, out double z, out double m)
        {
            x = y = z = m = 0;
            if (_tree is null || _tree.NodeCount == 0) return false;
            var root = _tree.GetNode(_tree.NaturalUnitIndex());
            unsafe
            {
                x = root.Coord[0];
                y = root.Coord[1];
                z = root.Coord[2];
                m = root.Coord[3];
            }
            return true;
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
/// Video frames share the image ladder. Their roots and coordinates accumulate during
/// the ordinary ordered witness drain; the terminal vendor record composes one video
/// trajectory without manufacturing membership or adjacency testimony.
/// </summary>
public sealed class VideoFrameIngestHandler : IIngestRecordHandler<VideoIngestRecord>
{
    private readonly Hash128 _sourceId;
    private readonly int _layerOrder;
    private readonly ImageIngestHandler _imageHandler;
    private readonly SortedDictionary<int, FramePlacement> _frames = new();
    public bool IgnoreCompletedFiles { get; init; }

    public VideoFrameIngestHandler(Hash128 sourceId, int layerOrder)
    {
        _sourceId = sourceId;
        _layerOrder = layerOrder;
        _imageHandler = new ImageIngestHandler(sourceId, layerOrder);
    }

    public IIngestDeferredUnit CreateDeferredUnit(VideoIngestRecord record) => record switch
    {
        VideoIngestRecord.Frame(var frame) => _imageHandler.CreateDeferredUnit(
            new ImageIngestRecord(frame.Rgba, frame.Width, frame.Height, frame.SourceId, frame.Metadata)),
        VideoIngestRecord.SequenceEnd => SequenceEndUnit.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(record)),
    };

    public long UnitsPerRecord(VideoIngestRecord record) =>
        record is VideoIngestRecord.Frame ? 1 : 0;

    public void WalkWitness(
        VideoIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        switch (record)
        {
            case VideoIngestRecord.Frame(var frame):
                _imageHandler.WalkWitness(
                    new ImageIngestRecord(
                        frame.Rgba, frame.Width, frame.Height, frame.SourceId, frame.Metadata),
                    root, builder, unit);
                if (root == default) return;
                if (unit is not IResolvedRootCoordinateUnit coordinate
                    || !coordinate.TryGetRootCoordinate(out double x, out double y, out double z, out double m))
                    throw new InvalidOperationException(
                        $"video frame {frame.FrameIndex} composed without a recoverable root coordinate");
                _frames[frame.FrameIndex] = new FramePlacement(root, x, y, z, m);
                break;

            case VideoIngestRecord.SequenceEnd:
                StageVideoTrajectory(builder, _frames, _sourceId);
                break;
        }
    }

    internal static Hash128 StageVideoTrajectory(
        SubstrateChangeBuilder builder,
        IReadOnlyDictionary<int, FramePlacement> frames,
        Hash128 sourceId)
    {
        if (frames.Count == 0) return default;

        var roots = new Hash128[frames.Count];
        var coords = new double[frames.Count * 4];
        int expected = 0;
        foreach (var (ordinal, frame) in frames.OrderBy(static p => p.Key))
        {
            if (ordinal != expected)
                throw new InvalidOperationException(
                    $"video trajectory is not contiguous: expected frame {expected}, found {ordinal}");
            roots[expected] = frame.Root;
            coords[expected * 4 + 0] = frame.X;
            coords[expected * 4 + 1] = frame.Y;
            coords[expected * 4 + 2] = frame.Z;
            coords[expected * 4 + 3] = frame.M;
            expected++;
        }

        Hash128 videoRoot = HashVideoRoot(roots);
        double[] center = Math4d.KarcherMean(coords);
        var physicality = new PhysicalityRow(
            Id: PhysicalityId.Compute(videoRoot, PhysicalityType.Content),
            EntityId: videoRoot,
            SourceId: sourceId,
            Type: PhysicalityType.Content,
            CoordX: center[0], CoordY: center[1], CoordZ: center[2], CoordM: center[3],
            HilbertIndex: Hilbert128.Encode(center),
            TrajectoryXyzm: Trajectory.Build(roots),
            NConstituents: roots.Length,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000);

        builder.AddEntity(videoRoot, EntityTier.Document, EntityTypeRegistry.Video, sourceId);
        builder.AddPhysicality(physicality);
        return videoRoot;
    }

    public static Hash128 HashVideoRoot(IReadOnlyList<Hash128> orderedFrameRoots)
    {
        ReadOnlySpan<byte> domain = "substrate/video/v1/frames"u8;
        var buf = new byte[domain.Length + orderedFrameRoots.Count * 16];
        domain.CopyTo(buf);
        for (int i = 0; i < orderedFrameRoots.Count; i++)
            orderedFrameRoots[i].WriteBytes(buf.AsSpan(domain.Length + i * 16, 16));
        return Hash128.Blake3(buf);
    }

    internal readonly record struct FramePlacement(
        Hash128 Root, double X, double Y, double Z, double M);

    private sealed class SequenceEndUnit : IIngestDeferredUnit
    {
        internal static readonly SequenceEndUnit Instance = new();
        public TierTree? TreeForBatchProbe => null;
        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) => default;
        public void Dispose() { }
    }
}

public static class MediaIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, double witnessWeight, string batchLabelPrefix,
        ISubstrateReader? reader, IngestSourceProfile profile, DecomposerOptions? options = null)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            WitnessWeight = witnessWeight,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
