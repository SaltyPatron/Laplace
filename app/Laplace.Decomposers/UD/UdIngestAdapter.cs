using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.UD;

public sealed class UdConlluFileStream : IRecordStream<UdIngestRecord>
{
    private readonly string _path;
    private readonly Hash128 _langId;
    private readonly string _langCode;

    public UdConlluFileStream(string path, Hash128 langId, string langCode)
    {
        _path = path;
        _langId = langId;
        _langCode = langCode;
    }

    public async IAsyncEnumerable<UdIngestRecord> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var sentence in UdConlluParser.ParseSentencesAsync(_path, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return new UdIngestRecord(sentence, _langId, _langCode);
        }
    }
}

public sealed class UdListRecordStream(IReadOnlyList<UdIngestRecord> records) : IRecordStream<UdIngestRecord>
{
    public async IAsyncEnumerable<UdIngestRecord> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return records[i];
        }
    }
}

internal sealed class UdContentForest : IDisposable
{
    private readonly List<CanonicalRoot> _canonicals;
    private readonly Dictionary<Hash128, NodeLocation> _locations;

    private UdContentForest(
        List<CanonicalRoot> canonicals,
        List<TierTree?> trees,
        Dictionary<Hash128, NodeLocation> locations)
    {
        _canonicals = canonicals;
        Trees = trees;
        _locations = locations;
    }

    internal IReadOnlyList<TierTree?> Trees { get; }

    internal static UdContentForest Build(UdSentence sentence)
    {
        var surfaces = new List<byte[]>();
        UdSentenceEmitContext.CollectCanonicals(sentence, surfaces);

        var canonicals = new List<CanonicalRoot>(surfaces.Count);
        var needed = new HashSet<Hash128>();
        foreach (byte[] surface in surfaces)
        {
            if (ContentTierSpine.ResolveRoot(surface) is not { } rootId) continue;
            canonicals.Add(new CanonicalRoot(surface, rootId));
            needed.Add(rootId);
        }

        var trees = new List<TierTree?>(canonicals.Count);
        var locations = new Dictionary<Hash128, NodeLocation>();
        foreach (CanonicalRoot canonical in canonicals)
        {
            if (locations.ContainsKey(canonical.RootId)) continue;
            TierTree? tree = ContentTierSpine.BuildTree(canonical.Canonical);
            if (tree is null) continue;
            int treeIndex = trees.Count;
            trees.Add(tree);

            Hash128[] ids = tree.NodeIds();
            for (int nodeIndex = 0; nodeIndex < ids.Length; nodeIndex++)
            {
                Hash128 id = ids[nodeIndex];
                if (needed.Contains(id) && !locations.ContainsKey(id))
                    locations.Add(id, new NodeLocation(treeIndex, (uint)nodeIndex));
            }
        }
        return new UdContentForest(canonicals, trees, locations);
    }

    internal void RegisterPlacements(
        UdSentenceEmitContext context, ReadOnlySpan<bool> emittedTrees)
    {
        foreach (CanonicalRoot canonical in _canonicals)
        {
            if (!_locations.TryGetValue(canonical.RootId, out NodeLocation location)
                || location.TreeIndex >= emittedTrees.Length
                || !emittedTrees[location.TreeIndex]
                || Trees[location.TreeIndex] is not { } tree)
                continue;

            TierNodeView node = tree.GetNode(location.NodeIndex);
            unsafe
            {
                ReadOnlySpan<double> coord = new(node.Coord, 4);
                context.RegisterRoot(canonical.Canonical, canonical.RootId, coord);
            }
        }
    }

    public void Dispose()
    {
        foreach (TierTree? tree in Trees) tree?.Dispose();
    }

    private readonly record struct CanonicalRoot(byte[] Canonical, Hash128 RootId);
    private readonly record struct NodeLocation(int TreeIndex, uint NodeIndex);
}

public sealed class UdIngestHandler : IIngestRecordHandler<UdIngestRecord>, IIngestBatchScopedHandler
{
    private readonly Hash128 _sourceId;
    private readonly string _fileLabel;
    private readonly ConcurrentDictionary<string, byte> _canonicalNames;
    private readonly HashSet<Hash128> _seenEntBatch = new();
    private readonly ConcurrentIdSet _seenSourceDeclarations;
    private UdSentenceEmitContext? _emitCtx;

    public UdIngestHandler(
        Hash128 sourceId,
        ConcurrentDictionary<string, byte> canonicalNames,
        string fileLabel = "ud/in-memory",
        ConcurrentIdSet? seenSourceDeclarations = null)
    {
        _sourceId = sourceId;
        _fileLabel = fileLabel;
        _canonicalNames = canonicalNames;
        _seenSourceDeclarations = seenSourceDeclarations ?? new ConcurrentIdSet();
    }

    public IIngestDeferredUnit CreateDeferredUnit(UdIngestRecord record) =>
        new UdDeferredUnit(record.Sentence, _sourceId, this);

    public void WalkWitness(UdIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (_emitCtx is null) return;
        UdSentenceEmitContext.EmitWitness(
            builder, record.Sentence, record.LangId, record.LangCode, _fileLabel,
            _seenEntBatch, _seenSourceDeclarations, _canonicalNames, _emitCtx, _sourceId);
        _emitCtx = null;
    }

    internal void SetEmitContext(UdSentenceEmitContext ctx) => _emitCtx = ctx;

    public void ResetBatchState()
    {
        _seenEntBatch.Clear();
    }

    private sealed class UdDeferredUnit : IMultiTreeIngestDeferredUnit
    {
        private readonly UdSentence _sentence;
        private readonly Hash128 _sourceId;
        private readonly UdIngestHandler _handler;
        private UdContentForest? _forest;
        private bool _disposed;

        public UdDeferredUnit(UdSentence sentence, Hash128 sourceId, UdIngestHandler handler)
        {
            _sentence = sentence;
            _sourceId = sourceId;
            _handler = handler;
        }

        public TierTree? TreeForBatchProbe => AllProbeTrees.Count > 0 ? AllProbeTrees[0] : null;

        public IReadOnlyList<TierTree?> AllProbeTrees
        {
            get
            {
                if (_forest is not null) return _forest.Trees;
                EnsureTrees();
                return _forest!.Trees;
            }
        }

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct)
        {
            var trees = AllProbeTrees;
            if (trees.Count == 0) return Task.FromResult<byte[]?>(null);
            return TierTreeContainmentProbe.ProbeNodeEmitBitmapAsync(trees[0]!, reader, ct);
        }

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            if (descentBitmap is null)
                return DrainInto(builder, witnessWeight, ReadOnlySpan<byte[]?>.Empty);
            byte[]?[] one = [descentBitmap];
            return DrainInto(builder, witnessWeight, one);
        }

        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, ReadOnlySpan<byte[]?> perTreeBitmaps)
        {
            EnsureTrees();
            var ctx = new UdSentenceEmitContext();
            IReadOnlyList<TierTree?> trees = _forest!.Trees;
            var emitted = new bool[trees.Count];
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] is not { } tree) continue;
                ReadOnlySpan<byte> bm = perTreeBitmaps.Length > i && perTreeBitmaps[i] is { } b
                    ? b
                    : ReadOnlySpan<byte>.Empty;
                emitted[i] = ContentTierSpine.EmitTree(
                    builder, tree, _sourceId, bm, out _);
            }
            _forest.RegisterPlacements(ctx, emitted);
            _handler.SetEmitContext(ctx);
            return default;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _forest?.Dispose();
            _forest = null;
        }

        private void EnsureTrees()
        {
            _forest ??= UdContentForest.Build(_sentence);
        }
    }
}

public static class UdIngestSupport
{
    public static string FileLabel(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        string? treebank = Path.GetDirectoryName(path) is { } directory
            ? Path.GetFileName(directory)
            : null;
        return string.IsNullOrEmpty(treebank)
            ? $"ud/{stem}"
            : $"ud/{treebank}/{stem}";
    }

    public static string ExtractLangCode(string fileName)
    {
        int under = fileName.IndexOf('_');
        return under > 0 ? fileName[..under] : "und";
    }

    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, string batchLabelPrefix, int batchSentences, ISubstrateReader? reader,
        long maxInputUnits = 0)
    {
        var profile = IngestSourceProfile.UdSentence;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSentences);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 64, 512),
            ContainmentReader = reader,
            MaxInputUnits = maxInputUnits,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = ws.Batch * 40,
            PhysicalityCapacity = ws.Batch * 40,
            AttestationCapacity = ws.Batch * 8,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static int ResolveBatchSentences(DecomposerOptions options) =>
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.UdSentence, options);
}
