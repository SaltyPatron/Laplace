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

public sealed class UdConlluMultiFileStream : IMultiFileRecordStream<UdIngestRecord>
{
    private readonly IReadOnlyList<(string Path, string Label)> _files;

    public UdConlluMultiFileStream(IReadOnlyList<(string Path, string Label)> files) => _files = files;

    public async IAsyncEnumerable<(string FileLabel, UdIngestRecord Record)> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (path, label) in _files)
        {
            string langCode = UdIngestSupport.ExtractLangCode(Path.GetFileName(path));
            Hash128 langId = LanguageReference.Resolve(langCode);
            await foreach (var sentence in UdConlluParser.ParseSentencesAsync(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return (label, new UdIngestRecord(sentence, langId, langCode));
            }
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

public sealed class UdIngestHandler : IIngestRecordHandler<UdIngestRecord>, IIngestBatchScopedHandler
{
    private readonly Hash128 _sourceId;
    private readonly ConcurrentDictionary<string, byte> _canonicalNames;
    private readonly HashSet<Hash128> _seenEntBatch = new();
    private ConcurrentIdSet _seenAttBatch = new();
    private UdSentenceEmitContext? _emitCtx;

    public UdIngestHandler(Hash128 sourceId, ConcurrentDictionary<string, byte> canonicalNames)
    {
        _sourceId = sourceId;
        _canonicalNames = canonicalNames;
    }

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        UdIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(UdIngestRecord record) =>
        new UdDeferredUnit(record.Sentence, _sourceId, this);

    public void WalkWitness(UdIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (_emitCtx is null) return;
        UdSentenceEmitContext.EmitWitness(
            builder, record.Sentence, record.LangId, record.LangCode,
            _seenEntBatch, _seenAttBatch, _canonicalNames, _emitCtx, _sourceId);
        _emitCtx = null;
    }

    internal void SetEmitContext(UdSentenceEmitContext ctx) => _emitCtx = ctx;

    public void ResetBatchState()
    {
        _seenEntBatch.Clear();
        _seenAttBatch = new ConcurrentIdSet();
    }

    private sealed class UdDeferredUnit : IMultiTreeIngestDeferredUnit
    {
        private readonly UdSentence _sentence;
        private readonly Hash128 _sourceId;
        private readonly UdIngestHandler _handler;
        private readonly List<ContentTreeEntry> _entries = new();
        private IReadOnlyList<TierTree?>? _probeTrees;
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
                if (_probeTrees is not null) return _probeTrees;
                EnsureTrees();
                return _probeTrees!;
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
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                ReadOnlySpan<byte> bm = perTreeBitmaps.Length > i && perTreeBitmaps[i] is { } b
                    ? b
                    : ReadOnlySpan<byte>.Empty;
                if (ContentTierSpine.EmitTree(builder, e.Tree, _sourceId, bm, out var rootId))
                    ctx.RegisterRoot(e.Canonical, rootId);
            }
            _handler.SetEmitContext(ctx);
            return default;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var e in _entries) e.Tree.Dispose();
            _entries.Clear();
            _probeTrees = null;
        }

        private void EnsureTrees()
        {
            if (_probeTrees is not null) return;
            var canonicals = new List<byte[]>();
            UdSentenceEmitContext.CollectCanonicals(_sentence, canonicals);
            var trees = new List<TierTree?>(canonicals.Count);
            foreach (var canonical in canonicals)
            {
                var tree = ContentTierSpine.BuildTree(canonical);
                if (tree is null) continue;
                trees.Add(tree);
                _entries.Add(new ContentTreeEntry(canonical, tree));
            }
            _probeTrees = trees;
        }

        private readonly record struct ContentTreeEntry(byte[] Canonical, TierTree Tree);
    }
}

public static class UdIngestSupport
{
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
            AttestationCapacity = ws.Batch * 60,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static int ResolveBatchSentences(DecomposerOptions options) =>
        IngestSizing.ResolveForSource(IngestSourceProfile.UdSentence, options.BatchSize > 1 ? options.BatchSize : null)
            .RecordBatchSize;
}
