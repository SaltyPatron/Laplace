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
/// The record every relation-triple provider yields: two canonical (underscore-normalized)
/// content phrases and the relation between them. Tier-tree composition, working-set
/// dedup, bulk COPY and the consensus fold are the shared pipeline (IngestBatchPipeline
/// driving <see cref="RelationTripleHandler"/>). Magnitude is the source's weight for the
/// testimony (1.0 when the source states none).
/// </summary>
public readonly record struct RelationTripleRecord(
    byte[] SubjectCanonical,
    string RelationType,
    /// <summary>
    /// Null means the source stated that no object exists for this (subject, relation), not
    /// that one was omitted: absence of a row is unknown, but a stated absence is testimony.
    /// The handler folds it as an object-null refutation of the (subject, relation) cell,
    /// matching <c>NativeAttestation.Categorical</c>'s nullable object. Only the provider knows
    /// how its grammar spells absence, so only the provider passes null; no content value is
    /// ever filtered as a sentinel. An empty array means the object failed to parse; that
    /// record stays unknown and is dropped.
    /// </summary>
    byte[]? ObjectCanonical,
    Hash128? ContextId = null,
    double Magnitude = 1.0,
    char? SubjectPos = null,
    char? ObjectPos = null,
    Hash128? SubjectSynsetId = null,
    Hash128? ObjectSynsetId = null,
    string? SubjectLangCode = null,
    string? ObjectLangCode = null,
    string? ContextAnchorKey = null,
    Hash128? ContextCategoryTypeId = null,
    /// <summary>
    /// Independent witnesses the source states for this triple (e.g. the length of
    /// ConceptNet's "sources" array); the fold counts them as observations. Defaults to 1.
    /// </summary>
    long ObservationCount = 1);

/// <summary>
/// Ingest handler shared by every relation-triple provider. Each record becomes a two-tree
/// deferred unit: subject and object phrases are composed and deduped independently
/// (IMultiTreeIngestDeferredUnit), then the categorical attestation is emitted between
/// their semantic anchors when the source supplied them, otherwise between the content
/// roots. Providers differ only in how they extract records.
/// </summary>
public sealed class RelationTripleHandler : IIngestRecordHandler<RelationTripleRecord>
{
    private readonly Hash128 _sourceId;
    private readonly double _sourceTrust;
    private readonly ConcurrentIdSet? _sourceNodeDeclarations;

    public RelationTripleHandler(
        Hash128 sourceId, double sourceTrust,
        ConcurrentIdSet? sourceNodeDeclarations = null)
    {
        _sourceId = sourceId;
        _sourceTrust = sourceTrust;
        _sourceNodeDeclarations = sourceNodeDeclarations;
    }

    public IIngestDeferredUnit CreateDeferredUnit(RelationTripleRecord record) =>
        new TripleDeferredUnit(record, _sourceId, _sourceTrust, _sourceNodeDeclarations);

    // Emission happens in the unit's DrainInto, which holds both trees and the attestation.
    public void WalkWitness(RelationTripleRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    /// <summary>
    /// Existence-gate path: both phrases are already present, so neither tier tree is
    /// recomposed, but the record's testimony (relation plus POS/synset/language facts) is
    /// still emitted exactly as DrainInto would.
    /// </summary>
    internal void WitnessPresentPair(
        in RelationTripleRecord record, Hash128 subjectRoot, Hash128 objectRoot,
        SubstrateChangeBuilder builder) =>
        EmitTripleFacts(
            builder, in record, subjectRoot, objectRoot,
            _sourceId, _sourceTrust, _sourceNodeDeclarations);

    // The record's attestations given both content roots; used by DrainInto (composed
    // roots) and by the existence-gate path (roots resolved without composing).
    private static void EmitTripleFacts(
        SubstrateChangeBuilder builder, in RelationTripleRecord record,
        Hash128 subjectRoot, Hash128 objectRoot, Hash128 sourceId, double sourceTrust,
        ConcurrentIdSet? sourceNodeDeclarations)
    {
        Hash128 subjectEndpoint = record.SubjectSynsetId is { } ss && ss != default
            ? ss : subjectRoot;
        Hash128 objectEndpoint = record.ObjectSynsetId is { } os && os != default
            ? os : objectRoot;
        // A stated absence is testimony: an object-null refutation of the (subject, relation)
        // cell. It precedes the paired arm because there is no object endpoint.
        if (subjectEndpoint != default && record.ObjectCanonical is null)
        {
            builder.AddAttestation(NativeAttestation.Categorical(
                subjectEndpoint, record.RelationType, null, sourceId, sourceTrust,
                contextId: record.ContextId, confirm: false,
                observationCount: record.ObservationCount));
        }
        else if (subjectEndpoint != default && objectEndpoint != default)
        {
            Hash128? ctx = record.ContextId;
            if (record.ContextAnchorKey is { Length: > 0 } ctxKey
                && record.ContextCategoryTypeId is { } ctxType && ctxType != default)
            {
                ctx = AnchorAdmission.Emit(builder, ctxKey, ctxType, sourceId, sourceTrust) ?? ctx;
            }
            builder.AddAttestation(NativeAttestation.Categorical(
                subjectEndpoint, record.RelationType, objectEndpoint, sourceId, sourceTrust,
                magnitude: record.Magnitude, arenaScale: 1.0, contextId: ctx,
                observationCount: record.ObservationCount));
        }

        // Source-encoded POS (n/v/a/r/s) maps to the canonical POS entity through the
        // WordNet tagset; POS entities are seeded with the foundation, so the reference exists.
        if (subjectRoot != default && record.SubjectPos is { } sp)
            EmitPosDeclaration(
                builder, subjectRoot, sp, sourceId, sourceTrust, sourceNodeDeclarations);
        if (objectRoot != default && record.ObjectPos is { } op)
            EmitPosDeclaration(
                builder, objectRoot, op, sourceId, sourceTrust, sourceNodeDeclarations);

        EmitSynsetMembership(
            builder, subjectRoot, record.SubjectSynsetId,
            sourceId, sourceTrust, sourceNodeDeclarations);
        EmitSynsetMembership(
            builder, objectRoot, record.ObjectSynsetId,
            sourceId, sourceTrust, sourceNodeDeclarations);

        if (subjectRoot != default && record.SubjectLangCode is { Length: > 0 } subjectLang)
        {
            Hash128 sl = LanguageReference.Emit(
                builder, subjectLang, sourceId, sourceTrust);
            AddSourceNodeDeclaration(builder, NativeAttestation.Categorical(
                subjectRoot, "HAS_LANGUAGE", sl, sourceId, sourceTrust),
                sourceNodeDeclarations);
        }
        if (objectRoot != default && record.ObjectLangCode is { Length: > 0 } objectLang)
        {
            Hash128 ol = LanguageReference.Emit(
                builder, objectLang, sourceId, sourceTrust);
            AddSourceNodeDeclaration(builder, NativeAttestation.Categorical(
                objectRoot, "HAS_LANGUAGE", ol, sourceId, sourceTrust),
                sourceNodeDeclarations);
        }
    }

    private static void EmitPosDeclaration(
        SubstrateChangeBuilder builder, Hash128 nodeRoot, char pos,
        Hash128 sourceId, double sourceTrust, ConcurrentIdSet? sourceNodeDeclarations)
    {
        Hash128 posId = PosReference.Resolve(
            pos.ToString(), PosReference.PosTagset.WordNet, out _);
        AttestationRow declaration = NativeAttestation.CategoricalResolved(
            nodeRoot, PosReference.HasPosTypeId, posId, sourceId, null, sourceTrust);
        if (sourceNodeDeclarations is not null && !sourceNodeDeclarations.Add(declaration.Id)) return;
        PosReference.Attest(
            builder, nodeRoot, pos.ToString(), PosReference.PosTagset.WordNet,
            sourceId, null, sourceTrust);
    }

    private static void EmitSynsetMembership(
        SubstrateChangeBuilder builder, Hash128 nodeRoot, Hash128? synId,
        Hash128 sourceId, double sourceTrust, ConcurrentIdSet? sourceNodeDeclarations)
    {
        if (nodeRoot == default || synId is not { } syn || syn == default) return;
        AddSourceNodeDeclaration(builder, NativeAttestation.Categorical(
            nodeRoot, "CORRESPONDS_TO", syn, sourceId, sourceTrust), sourceNodeDeclarations);
    }

    private static void AddSourceNodeDeclaration(
        SubstrateChangeBuilder builder, AttestationRow declaration,
        ConcurrentIdSet? sourceNodeDeclarations)
    {
        if (sourceNodeDeclarations is null || sourceNodeDeclarations.Add(declaration.Id))
            builder.AddAttestation(declaration);
    }

    private sealed class TripleDeferredUnit : IMultiTreeIngestDeferredUnit
    {
        private readonly RelationTripleRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _sourceTrust;
        private readonly ConcurrentIdSet? _sourceNodeDeclarations;
        private TierTree? _subjectTree;
        private TierTree? _objectTree;
        private readonly TierTree?[] _trees;
        private bool _disposed;

        public TripleDeferredUnit(
            RelationTripleRecord record, Hash128 sourceId, double sourceTrust,
            ConcurrentIdSet? sourceNodeDeclarations)
        {
            _record = record;
            _sourceId = sourceId;
            _sourceTrust = sourceTrust;
            _sourceNodeDeclarations = sourceNodeDeclarations;
            // Trees are built here because CreateDeferredUnit runs in the parallel stage,
            // not the sequential drain. A malformed phrase yields a null tree, not a throw.
            _subjectTree = TryBuild(record.SubjectCanonical);
            _objectTree = TryBuild(record.ObjectCanonical);
            _trees = [_subjectTree, _objectTree];
        }

        private static TierTree? TryBuild(byte[]? canonical)
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
                    $"RelationTriple: tier-tree build failed ({canonical.Length} bytes): {ex.Message}");
                return null;
            }
        }

        // Single-tree interface member; the multi-tree path does not use it.
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

            EmitTripleFacts(
                builder, in _record, subjectRoot, objectRoot,
                _sourceId, _sourceTrust, _sourceNodeDeclarations);

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
