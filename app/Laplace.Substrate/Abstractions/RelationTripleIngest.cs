using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Adapts a decomposer's lazy record extraction to the pipeline's IRecordStream.</summary>
public sealed class AsyncEnumerableRecordStream<T>(IAsyncEnumerable<T> source) : IRecordStream<T>
{
    public async IAsyncEnumerable<T> RecordsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct))
            yield return item;
    }
}

/// <summary>
/// The one generic record every relation-triple source emits: two already-canonical
/// (underscore-normalized) content phrases and the edge between them. A decomposer's
/// ONLY job is to yield these; everything downstream — perfcache tier-tree build,
/// working-set descent dedup, bulk COPY, Glicko fold — is the shared pipeline
/// (IngestBatchPipeline working-set mode driving RelationTripleHandler). Magnitude
/// carries a source-supplied edge weight (1.0 when the source has none).
/// </summary>
public readonly record struct RelationTripleRecord(
    byte[] SubjectCanonical,
    string RelationType,
    byte[] ObjectCanonical,
    Hash128? ContextId = null,
    double Magnitude = 1.0,
    char? SubjectPos = null,
    char? ObjectPos = null,
    Hash128? SubjectSynsetId = null,
    Hash128? ObjectSynsetId = null,
    Hash128? SubjectLangId = null,
    Hash128? ObjectLangId = null,
    string? ContextAnchorKey = null,
    Hash128? ContextCategoryTypeId = null);

/// <summary>
/// The single ingestion handler for ALL relation-triple sources. Each record becomes a
/// two-tree deferred unit: the subject and object phrases are content-decomposed and
/// descent-deduped independently (IMultiTreeIngestDeferredUnit), then the folding
/// Categorical edge is emitted between their content-addressed roots. Written once;
/// atomic, conceptnet, and any future triple source share it verbatim and differ only
/// in how they extract records.
/// </summary>
public sealed class RelationTripleHandler : IIngestRecordHandler<RelationTripleRecord>
{
    private readonly Hash128 _sourceId;
    private readonly double _sourceTrust;

    public RelationTripleHandler(Hash128 sourceId, double sourceTrust)
    {
        _sourceId = sourceId;
        _sourceTrust = sourceTrust;
    }

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        RelationTripleRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(RelationTripleRecord record) =>
        new TripleDeferredUnit(record, _sourceId, _sourceTrust);

    // Emission happens in the unit's DrainInto (it owns both trees + the edge); nothing to add here.
    public void WalkWitness(RelationTripleRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    /// <summary>
    /// Existence-gate short-circuit: both phrases are proven present, so neither tier tree
    /// needs recomposing — but the record's testimony (edge + POS/synset/language facts)
    /// must still be emitted, exactly as DrainInto would have.
    /// </summary>
    internal void WitnessPresentPair(
        in RelationTripleRecord record, Hash128 subjectRoot, Hash128 objectRoot,
        SubstrateChangeBuilder builder) =>
        EmitTripleFacts(builder, in record, subjectRoot, objectRoot, _sourceId, _sourceTrust);

    // The record's full attested payload given both content roots — shared verbatim by the
    // deferred unit's DrainInto (composed roots) and the existence-gate short-circuit
    // (roots resolved without compose).
    private static void EmitTripleFacts(
        SubstrateChangeBuilder builder, in RelationTripleRecord record,
        Hash128 subjectRoot, Hash128 objectRoot, Hash128 sourceId, double sourceTrust)
    {
        if (subjectRoot != default && objectRoot != default)
        {
            Hash128? ctx = record.ContextId;
            if (record.ContextAnchorKey is { Length: > 0 } ctxKey
                && record.ContextCategoryTypeId is { } ctxType && ctxType != default)
            {
                ctx = CategoryAnchor.Emit(builder, ctxKey, ctxType, sourceId, sourceTrust) ?? ctx;
            }
            builder.AddAttestation(NativeAttestation.Categorical(
                subjectRoot, record.RelationType, objectRoot, sourceId, sourceTrust,
                magnitude: record.Magnitude, arenaScale: 1.0, contextId: ctx));
        }

        // Fold source-encoded POS onto the unified POS hub (n/v/a/r/s → canonical via the
        // WordNet tagset). POS entities are foundation-seeded, so this is FK-safe.
        if (subjectRoot != default && record.SubjectPos is { } sp)
            PosReference.Attest(builder, subjectRoot, sp.ToString(),
                PosReference.PosTagset.WordNet, sourceId, null, sourceTrust);
        if (objectRoot != default && record.ObjectPos is { } op)
            PosReference.Attest(builder, objectRoot, op.ToString(),
                PosReference.PosTagset.WordNet, sourceId, null, sourceTrust);

        EmitSynsetMembership(builder, subjectRoot, record.SubjectSynsetId, sourceId, sourceTrust);
        EmitSynsetMembership(builder, objectRoot, record.ObjectSynsetId, sourceId, sourceTrust);

        if (subjectRoot != default && record.SubjectLangId is { } sl && sl != default)
        {
            builder.AddEntity(new EntityRow(
                sl, EntityTier.Word, EntityTypeRegistry.Language, sourceId));
            builder.AddAttestation(NativeAttestation.Categorical(
                subjectRoot, "HAS_LANGUAGE", sl, sourceId, sourceTrust));
        }
        if (objectRoot != default && record.ObjectLangId is { } ol && ol != default)
        {
            builder.AddEntity(new EntityRow(
                ol, EntityTier.Word, EntityTypeRegistry.Language, sourceId));
            builder.AddAttestation(NativeAttestation.Categorical(
                objectRoot, "HAS_LANGUAGE", ol, sourceId, sourceTrust));
        }
    }

    private static void EmitSynsetMembership(
        SubstrateChangeBuilder builder, Hash128 nodeRoot, Hash128? synId,
        Hash128 sourceId, double sourceTrust)
    {
        if (nodeRoot == default || synId is not { } syn || syn == default) return;
        builder.AddAttestation(NativeAttestation.Categorical(
            nodeRoot, "CORRESPONDS_TO", syn, sourceId, sourceTrust));
    }

    private sealed class TripleDeferredUnit : IMultiTreeIngestDeferredUnit
    {
        private readonly RelationTripleRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _sourceTrust;
        private TierTree? _subjectTree;
        private TierTree? _objectTree;
        private readonly TierTree?[] _trees;
        private bool _disposed;

        public TripleDeferredUnit(RelationTripleRecord record, Hash128 sourceId, double sourceTrust)
        {
            _record = record;
            _sourceId = sourceId;
            _sourceTrust = sourceTrust;
            // Built here on purpose: CreateDeferredUnit is the fanned-out P-core stage,
            // so the CPU-heavy tier-tree build parallelizes instead of running in the
            // sequential drain. Defensive — a malformed phrase yields a null tree, not a throw.
            _subjectTree = TryBuild(record.SubjectCanonical);
            _objectTree = TryBuild(record.ObjectCanonical);
            _trees = [_subjectTree, _objectTree];
        }

        private static TierTree? TryBuild(byte[] canonical)
        {
            if (canonical is null || canonical.Length == 0) return null;
            try { return ContentTierSpine.BuildTree(canonical); }
            catch (OverflowException ex)
            {
                throw new OverflowException(
                    $"RelationTriple: tier-tree build overflow ({canonical.Length} byte phrase)", ex);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "RelationTriple: tier-tree build failed ({Bytes} bytes): {Message}",
                    canonical.Length, ex.Message);
                return null;
            }
        }

        // Base (single-tree) surface — unused on the multi path, present for the contract.
        public TierTree? TreeForBatchProbe => _subjectTree;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _subjectTree is null
                ? Task.FromResult<byte[]?>(null)
                : ContentTierSpine.ExistenceEmitBitmapAsync(_subjectTree, reader, ct);

        public IReadOnlyList<TierTree?> AllProbeTrees => _trees;

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
            DrainInto(builder, witnessWeight, new ReadOnlySpan<byte[]?>(_singleBitmap(descentBitmap)));

        private static byte[]?[] _singleBitmap(byte[]? bm) => [bm, null];

        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, ReadOnlySpan<byte[]?> perTreeBitmaps)
        {
            Hash128 subjectRoot = EmitTree(builder, _subjectTree, perTreeBitmaps.Length > 0 ? perTreeBitmaps[0] : null);
            Hash128 objectRoot = EmitTree(builder, _objectTree, perTreeBitmaps.Length > 1 ? perTreeBitmaps[1] : null);

            EmitTripleFacts(builder, in _record, subjectRoot, objectRoot, _sourceId, _sourceTrust);

            return subjectRoot;
        }

        private Hash128 EmitTree(SubstrateChangeBuilder builder, TierTree? tree, byte[]? bitmap)
        {
            if (tree is null) return default;
            return ContentTierSpine.EmitTree(
                builder, tree, _sourceId, bitmap ?? ReadOnlySpan<byte>.Empty, out var root) ? root : default;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _subjectTree?.Dispose();
            _objectTree?.Dispose();
            _subjectTree = null;
            _objectTree = null;
        }
    }
}
