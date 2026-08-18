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
    Hash128? ParentContainerId = null,
    // Content-DAG root of the file's own bytes (FileEntity.SourceId), when the caller
    // already knows it (RepoDecomposer does, per GH #592) — lets IngestExistenceGate
    // true-skip a file whose content is already witnessed anywhere, BEFORE paying the
    // tree-sitter parse cost, while WalkWitness still re-emits the caller-specific
    // facts (repo CONTAINS, concept-anchor links) that don't dedup away with the
    // content: two repos containing the identical file are two distinct CONTAINS
    // edges on the same content node, never one collapsed into the other.
    Hash128? SourceId = null);

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

    public IIngestDeferredUnit CreateDeferredUnit(GrammarComposeRecord record) =>
        new Unit(record, _sourceId, _trust, _reader);

    /// <summary>
    /// Runs for BOTH a fresh compose (unit is the real <see cref="Unit"/>, root came
    /// from decomposing this call) and a content-already-known short-circuit (unit is
    /// <see cref="PresentRootDeferredUnit"/>, IngestExistenceGate resolved root from
    /// record.SourceId without ever parsing the file — see GH #592). The concept-anchor
    /// converse.links(repo CONTAINS this file, file HAS_DEFINITION this concept) are re-emitted
    /// either way: they are per-CALLER facts, not per-CONTENT facts, so two repos
    /// containing byte-identical content still get two distinct CONTAINS edges on the
    /// one content node, never one silently dropped because the bytes were seen before.
    /// </summary>
    public void WalkWitness(
        GrammarComposeRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (root == default) return;
        EmitConceptLinks(builder, record, root, _sourceId, _trust);
    }

    private static void EmitConceptLinks(
        SubstrateChangeBuilder builder, GrammarComposeRecord record, Hash128 rootId, Hash128 sourceId, double trust)
    {
        if (record.ConceptAnchorKey is not { Length: > 0 }
            || record.ConceptCategoryTypeId is not { } ctype || ctype == default
            || CategoryAnchor.Emit(builder, record.ConceptAnchorKey, ctype, sourceId, trust) is not { } conceptId)
            return;

        if (record.ParentContainerId is { } parent && parent != default)
        {
            builder.AddAttestation(NativeAttestation.Categorical(
                parent, "CONTAINS", conceptId, sourceId, trust));
        }
        builder.AddAttestation(NativeAttestation.Categorical(
            conceptId, "HAS_EXAMPLE", rootId, sourceId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            rootId, "HAS_DEFINITION", conceptId, sourceId, trust));
    }

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

            // Concept-anchor converse.links(repo CONTAINS this file, file HAS_DEFINITION this
            // concept) moved to WalkWitness — the pipeline calls handler.WalkWitness
            // right after DrainInto on every novel record (IngestDescentFlush.cs) AND
            // on every content-already-known short-circuit (IngestExistenceGate.cs), so
            // emitting them there instead of here covers both paths from one call site
            // instead of duplicating the block (GH #592).

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
        Hash128 sourceId, string batchLabelPrefix, int batchSize, ISubstrateReader? reader)
    {
        var profile = IngestSourceProfile.Default;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSize);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 64, 1024),
            ContainmentReader = reader,
            EntityCapacity = ws.Batch * 8,
            PhysicalityCapacity = ws.Batch * 8,
            AttestationCapacity = ws.Batch * 16,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }

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
