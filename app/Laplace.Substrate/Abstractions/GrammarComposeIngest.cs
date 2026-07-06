using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One grammar-file record for sources that parse a whole file through tree-sitter
/// (Code, Stack, …). Extraction only; compose/existence/emit uses the shared pipeline.
/// </summary>
public readonly record struct GrammarComposeRecord(
    byte[] Utf8,
    string Modality,
    IReadOnlyList<string>? ExampleSegments = null,
    string? ConceptAnchorKey = null,
    Hash128? ConceptCategoryTypeId = null,
    IReadOnlyList<string>? KeywordExamples = null,
    Hash128? ParentContainerId = null);

/// <summary>
/// Single handler for whole-file grammar compose lanes. CreateDeferredUnit runs
/// GrammarEntityBuilder (with containment reader when present) on P-core workers;
/// DrainInto stages the result. TreeForBatchProbe is null — grammar rows carry their
/// own tier-tree probe inside BuildAsync when a reader is wired.
/// </summary>
public sealed class GrammarComposeHandler : IIngestRecordHandler<GrammarComposeRecord>
{
    private readonly Hash128 _sourceId;
    private readonly double _trust;
    private readonly ISubstrateReader? _reader;

    public GrammarComposeHandler(Hash128 sourceId, double trust, ISubstrateReader? reader)
    {
        _sourceId = sourceId;
        _trust = trust;
        _reader = reader;
    }

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        GrammarComposeRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(GrammarComposeRecord record) =>
        new Unit(record, _sourceId, _trust, _reader);

    public void WalkWitness(
        GrammarComposeRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    private sealed class Unit : IIngestDeferredUnit
    {
        private readonly GrammarComposeRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        private ImmutableArray<EntityRow> _ents;
        private ImmutableArray<PhysicalityRow> _phys;
        private ImmutableArray<AttestationRow> _atts;
        private Hash128 _rootId;
        private bool _disposed;

        public Unit(GrammarComposeRecord record, Hash128 sourceId, double trust, ISubstrateReader? reader)
        {
            _record = record;
            _sourceId = sourceId;
            _trust = trust;
            Build(reader);
        }

        public TierTree? TreeForBatchProbe => null;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            foreach (var e in _ents) builder.AddEntity(e);
            foreach (var p in _phys) builder.AddPhysicality(p);
            foreach (var a in _atts) builder.AddAttestation(a);

            if (_rootId != default && _record.ExampleSegments is { Count: > 0 })
            {
                foreach (var seg in _record.ExampleSegments)
                {
                    if (seg.Length < 3) continue;
                    if (ContentTierSpine.TryStageIntoBuilder(
                            builder, System.Text.Encoding.UTF8.GetBytes(seg), _sourceId, out var segRoot))
                    {
                        builder.AddAttestation(NativeAttestation.Categorical(
                            segRoot, "HAS_EXAMPLE", _rootId, _sourceId, _trust));
                    }
                }
            }

            if (_rootId != default
                && _record.ConceptAnchorKey is { Length: > 0 }
                && _record.ConceptCategoryTypeId is { } ctype && ctype != default
                && CategoryAnchor.Emit(builder, _record.ConceptAnchorKey, ctype, _sourceId, _trust) is { } conceptId)
            {
                if (_record.ParentContainerId is { } parent && parent != default)
                {
                    builder.AddAttestation(NativeAttestation.Categorical(
                        parent, "CONTAINS", conceptId, _sourceId, _trust));
                }
                builder.AddAttestation(NativeAttestation.Categorical(
                    conceptId, "HAS_EXAMPLE", _rootId, _sourceId, _trust));
                builder.AddAttestation(NativeAttestation.Categorical(
                    _rootId, "HAS_DEFINITION", conceptId, _sourceId, _trust));
            }

            if (_rootId != default && _record.KeywordExamples is { Count: > 0 })
            {
                foreach (var kw in _record.KeywordExamples)
                {
                    if (kw.Length < 4) continue;
                    if (ContentTierSpine.TryStageIntoBuilder(
                            builder, System.Text.Encoding.UTF8.GetBytes(kw), _sourceId, out var kwRoot))
                    {
                        builder.AddAttestation(NativeAttestation.Categorical(
                            kwRoot, "HAS_EXAMPLE", _rootId, _sourceId, _trust));
                    }
                }
            }

            return _rootId;
        }

        private void Build(ISubstrateReader? reader)
        {
            IntPtr recipe = GrammarDecomposer.LookupById(_record.Modality);
            if (recipe == IntPtr.Zero) return;
            try
            {
                using var ast = GrammarDecomposer.Parse(_record.Utf8, recipe);
                var geb = new GrammarEntityBuilder(
                    _record.Utf8, ast, _sourceId, _record.Modality, recipe,
                    GrammarTags.TagsSource(_record.Modality));
                if (reader is not null)
                    (_ents, _phys, _atts, _rootId) = geb.BuildAsync(_trust, reader).GetAwaiter().GetResult();
                else
                    (_ents, _phys, _atts, _rootId) = geb.Build(_trust);
            }
            catch
            {
                _ents = ImmutableArray<EntityRow>.Empty;
                _phys = ImmutableArray<PhysicalityRow>.Empty;
                _atts = ImmutableArray<AttestationRow>.Empty;
            }
        }

        public void Dispose() { if (_disposed) return; _disposed = true; }
    }
}

public static class GrammarComposeIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, string batchLabelPrefix, int batchSize, ISubstrateReader? reader) =>
        new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = Math.Max(1, batchSize),
            ProbeChunkSize = Math.Clamp(batchSize, 64, 1024),
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = batchSize * 8,
            PhysicalityCapacity = batchSize * 8,
            AttestationCapacity = batchSize * 16,
            WorkingSet = WorkingSetMode.Enabled,
        };

    public static IAsyncEnumerable<SubstrateChange> RunPipelineAsync(
        IAsyncEnumerable<GrammarComposeRecord> records,
        Hash128 sourceId,
        double trust,
        string batchLabelPrefix,
        int batchSize,
        ISubstrateReader? reader,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        if (options.DryRun) return Empty();
        var stream = new AsyncEnumerableRecordStream<GrammarComposeRecord>(records);
        var handler = new GrammarComposeHandler(sourceId, trust, reader);
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.GrammarCompose(sourceId, batchLabelPrefix, batchSize, options, reader),
            options);
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

    private static async IAsyncEnumerable<SubstrateChange> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
