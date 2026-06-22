using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

public enum IntentStageTable
{
    Entities      = 1,
    Physicalities = 2,
    Attestations  = 3,
}

public sealed class IntentStage : SafeHandle
{
    public const long PgEpochUnixUs = 946684800000000L;

    private IntentStage(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeInterop.IntentStageFree(handle);
        return true;
    }

    public static IntentStage New(int rowCapacityHint)
    {
        if (rowCapacityHint < 0) throw new ArgumentOutOfRangeException(nameof(rowCapacityHint));
        IntPtr h = NativeInterop.IntentStageNew((nuint)rowCapacityHint);
        if (h == IntPtr.Zero) throw new OutOfMemoryException("intent_stage_new returned NULL");
        return new IntentStage(h);
    }

    public int EntityCount      { get { ThrowIfDisposed(); return checked((int)NativeInterop.IntentStageEntityCount(handle)); } }
    public int PhysicalityCount { get { ThrowIfDisposed(); return checked((int)NativeInterop.IntentStagePhysicalityCount(handle)); } }
    public int AttestationCount { get { ThrowIfDisposed(); return checked((int)NativeInterop.IntentStageAttestationCount(handle)); } }

    public static string CopyColumnList(IntentStageTable table)
    {
        IntPtr p = NativeInterop.IntentStageCopyColumnList((int)table);
        return Marshal.PtrToStringUTF8(p)
            ?? throw new ArgumentOutOfRangeException(nameof(table));
    }

    public void AddEntity(Hash128 id, short tier, Hash128 typeId, Hash128? firstObservedBy)
    {
        ThrowIfDisposed();
        if (tier < 0 || tier > 255) throw new ArgumentOutOfRangeException(nameof(tier));
        unsafe
        {
            int rc;
            if (firstObservedBy is Hash128 fob)
            {
                rc = NativeInterop.IntentStageAddEntity(handle, &id, tier, &typeId, &fob);
            }
            else
            {
                rc = NativeInterop.IntentStageAddEntity(handle, &id, tier, &typeId, null);
            }
            if (rc != 0) throw new InvalidOperationException("intent_stage_add_entity failed");
        }
    }

    public void AddPhysicality(
        Hash128         id,
        Hash128         entityId,
        Hash128         sourceId,
        short           physicalityType,
        ReadOnlySpan<double> coord,
        Hilbert128      hilbertIndex,
        ReadOnlySpan<double> trajectoryXyzm,
        int             nConstituents,
        double?         alignmentResidual,
        int?            sourceDim,
        long            observedAtUnixUs)
    {
        ThrowIfDisposed();
        if (coord.Length < 4) throw new ArgumentException("coord must have 4 elements", nameof(coord));
        if (nConstituents < 0) throw new ArgumentOutOfRangeException(nameof(nConstituents));
        uint nVerts = (uint)(trajectoryXyzm.IsEmpty ? 0 : (trajectoryXyzm.Length / 4));
        if (!trajectoryXyzm.IsEmpty && trajectoryXyzm.Length % 4 != 0)
            throw new ArgumentException("trajectoryXyzm length must be a multiple of 4", nameof(trajectoryXyzm));

        unsafe
        {
            fixed (double* pCoord = coord)
            fixed (double* pTraj  = trajectoryXyzm)
            {
                int arNull = alignmentResidual is null ? 1 : 0;
                int sdNull = sourceDim          is null ? 1 : 0;
                double arVal = alignmentResidual ?? 0.0;
                int    sdVal = sourceDim          ?? 0;
                int rc = NativeInterop.IntentStageAddPhysicality(
                    handle, &id, &entityId, &sourceId, physicalityType, pCoord, &hilbertIndex,
                    nVerts == 0 ? null : pTraj, nVerts, nConstituents,
                    arNull, arVal, sdNull, sdVal, observedAtUnixUs);
                if (rc != 0) throw new InvalidOperationException("intent_stage_add_physicality failed");
            }
        }
    }

    public void AddAttestation(
        Hash128  id,
        Hash128  subjectId,
        Hash128  typeId,
        Hash128? objectId,
        Hash128  sourceId,
        Hash128? contextId,
        short    outcome,
        long     lastObservedAtUnixUs,
        long     observationCount)
    {
        ThrowIfDisposed();
        if (observationCount < 0) throw new ArgumentOutOfRangeException(nameof(observationCount));
        if (outcome is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(outcome));
        unsafe
        {
            int rc;
            Hash128 obj = objectId ?? default;
            Hash128 ctx = contextId ?? default;
            Hash128* objPtr = objectId is null ? null : &obj;
            Hash128* ctxPtr = contextId is null ? null : &ctx;
            rc = NativeInterop.IntentStageAddAttestation(
                handle, &id, &subjectId, &typeId, objPtr, &sourceId, ctxPtr,
                outcome, lastObservedAtUnixUs, observationCount);
            if (rc != 0) throw new InvalidOperationException("intent_stage_add_attestation failed");
        }
    }

    public byte[] EmitCopyBinary(IntentStageTable table)
    {
        ThrowIfDisposed();
        unsafe
        {
            nuint required = NativeInterop.IntentStageEmitCopyBinary(handle, (int)table, null, 0);
            var buf = new byte[checked((int)required)];
            fixed (byte* p = buf)
            {
                nuint written = NativeInterop.IntentStageEmitCopyBinary(handle, (int)table, p, required);
                if (written != required) throw new InvalidOperationException("intent_stage_emit_copy_binary wrote unexpected byte count");
            }
            return buf;
        }
    }

    public unsafe (IntPtr Ptr, long Len) TupleBuffer(IntentStageTable table)
    {
        ThrowIfDisposed();
        nuint len;
        byte* p = NativeInterop.IntentStageTuplePtr(handle, (int)table, &len);
        return ((IntPtr)p, checked((long)len));
    }

    public int EmitCopyBinary(IntentStageTable table, Span<byte> dest)
    {
        ThrowIfDisposed();
        unsafe
        {
            fixed (byte* p = dest)
            {
                nuint required = NativeInterop.IntentStageEmitCopyBinary(handle, (int)table, p, (nuint)dest.Length);
                return checked((int)required);
            }
        }
    }

    internal IntPtr DangerousNativeHandle => handle;

    
    
    
    
    
    
    
    public static void ResetContentBank() => NativeInterop.ContentWitnessReset();

    // When true, TryAddContentWitness bypasses the global native bank and emits via
    // BuildContentTree+EmitContentTree instead. The bank is a cross-run dedup optimisation
    // that grows monotonically until reset; for bulk-fresh loads the DB's ON CONFLICT /
    // NOT EXISTS handles uniqueness, so the bank buys nothing but OOMs on large corpora.
    private static volatile bool _bulkFreshBypass;
    public static void SetBulkFreshBypass(bool enabled) => _bulkFreshBypass = enabled;

    // Bounded, per-process "already emitted this run" cache, keyed by (content hash, source). In
    // bulk-fresh the native bank is bypassed and EmitContentTree gets an EMPTY bitmap => every
    // occurrence of repeated content (the word "the", shared graphemes/codepoints, synsets shared
    // across 1226 OMW languages) re-builds and re-stages the WHOLE tier tree, and the DB discards
    // the duplicates. That redundant client work is the measured client-bound bottleneck. A repeat
    // of the same (canonical, source) is deterministic — identical entity ids, coords, physicalities,
    // already staged on first sight — so returning the memoized root and skipping build+stage is
    // exact. Relations the caller wires via the root are unaffected (they are emitted outside this).
    // Bounded with clear-on-cap => cannot OOM (the unbounded growth that killed the old bank). A
    // dropped (evicted) entry just re-emits once and the DB de-dups it: a missed optimization, never
    // a correctness fault. Each seed source is its own process, so this cache is single-source.
    private const int EmittedTreeCap = 1 << 20;
    private static readonly ConcurrentDictionary<(Hash128 Content, Hash128 Source), Hash128> _emittedTrees = new();
    public static void ResetEmittedTreeCache() => _emittedTrees.Clear();

    public static bool IsContentWitnessProven(Hash128 id)
    {
        unsafe
        {
            Hash128 h = id;
            return NativeInterop.ContentWitnessEntityProven(&h) != 0;
        }
    }

    public bool TryAddContentWitness(ReadOnlySpan<byte> canonical, Hash128 sourceId, out Hash128 rootId)
    {
        rootId = default;
        if (canonical.IsEmpty) return false;
        ThrowIfDisposed();

        if (_bulkFreshBypass)
        {
            // Bulk-fresh: the global bank grows monotonically and OOMs on large corpora (e.g.
            // OMW 1226 files). Skip the bank and emit via the two-phase tree path — DB uniqueness
            // is guaranteed by ON CONFLICT DO NOTHING / NOT EXISTS, not the proven-set.
            // Repeat-skip: if this exact (canonical, source) was already emitted this run, the tree
            // is deterministic and already staged — return the memoized root without re-building or
            // re-staging it (the dominant client-bound cost on repetitive corpora).
            var cacheKey = (Hash128.Blake3(canonical), sourceId);
            if (_emittedTrees.TryGetValue(cacheKey, out var cachedRoot))
            {
                rootId = cachedRoot;
                return true;
            }
            var tree = BuildContentTree(canonical);
            if (tree is null) return false;
            bool emitted;
            using (tree) emitted = EmitContentTree(tree, sourceId, ReadOnlySpan<byte>.Empty, out rootId);
            if (emitted)
            {
                if (_emittedTrees.Count >= EmittedTreeCap) _emittedTrees.Clear();
                _emittedTrees[cacheKey] = rootId;
            }
            return emitted;
        }

        unsafe
        {
            Hash128 src = sourceId;
            Hash128 root = default;
            fixed (byte* utf8 = canonical)
            {
                int rc = NativeInterop.ContentWitnessBatchAdd(
                    handle, utf8, (nuint)canonical.Length, &src, &root);
                if (rc == -3) throw new InvalidOperationException(
                    "content witness requires the T0 perfcache — call CodepointPerfcache.LoadDefault() first");
                if (rc != 0) return false;
                rootId = root;
                return true;
            }
        }
    }

    /// <summary>
    /// Build the content tier tree for a UTF-8 span without emitting anything. The returned tree is
    /// owned by the caller (dispose it). First half of the two-phase containment path: build once,
    /// probe <see cref="TierTree.NodeIds"/> against the DB existing-bitmap, then emit only novel
    /// nodes via <see cref="EmitContentTree"/> — no second decomposition.
    /// </summary>
    public static TierTree? BuildContentTree(ReadOnlySpan<byte> canonical)
    {
        if (canonical.IsEmpty) return null;
        unsafe
        {
            IntPtr treePtr = IntPtr.Zero;
            fixed (byte* p = canonical)
            {
                int rc = NativeInterop.ContentWitnessTreeBuild(p, (nuint)canonical.Length, &treePtr);
                if (rc == -3) throw new InvalidOperationException(
                    "content witness requires the T0 perfcache — call CodepointPerfcache.LoadDefault() first");
                if (rc != 0 || treePtr == IntPtr.Zero) return null;
            }
            return TierTree.FromExistingHandle(treePtr);
        }
    }

    /// <summary>
    /// Emit a pre-built content tier tree. When <paramref name="existingBitmap"/> is non-empty only
    /// novel subtrees are staged (MerkleDedup.TrunkShortcircuit, indexed by tree node order); a
    /// present trunk skips its whole subtree. An empty bitmap emits all nodes. <paramref name="rootId"/>
    /// always receives the natural-unit root so attestations can be wired even when the subtree is skipped.
    /// </summary>
    public bool EmitContentTree(
        TierTree tree, Hash128 sourceId, ReadOnlySpan<byte> existingBitmap, out Hash128 rootId)
    {
        rootId = default;
        ArgumentNullException.ThrowIfNull(tree);
        ThrowIfDisposed();
        unsafe
        {
            Hash128 src = sourceId;
            Hash128 root = default;
            int rc;
            if (existingBitmap.IsEmpty)
            {
                rc = NativeInterop.ContentWitnessEmitTree(
                    handle, tree.DangerousNativeHandle, &src, null, 0, &root);
            }
            else
            {
                fixed (byte* bm = existingBitmap)
                {
                    rc = NativeInterop.ContentWitnessEmitTree(
                        handle, tree.DangerousNativeHandle, &src, bm, (nuint)tree.NodeCount, &root);
                }
            }
            if (rc == -3) throw new InvalidOperationException(
                "content witness requires the T0 perfcache — call CodepointPerfcache.LoadDefault() first");
            if (rc != 0) return false;
            rootId = root;
            return true;
        }
    }

    public bool WitnessContains(Hash128 id)
    {
        ThrowIfDisposed();
        unsafe
        {
            Hash128 h = id;
            return NativeInterop.IntentStageWitnessSeen(handle, &h) != 0;
        }
    }

    /// <summary>
    /// Bulk-drain a grammar compose result's entities + physicalities straight into this stage in one
    /// native call — no per-row managed P/Invoke. Within-batch dedup uses this stage's native witness
    /// set. PRECEDES are not drained here (they ride the managed attestation path).
    /// </summary>
    public void DrainComposeContent(IntPtr composeResult, Hash128 sourceId, long nowUs)
    {
        ThrowIfDisposed();
        int rc = NativeInterop.ComposeDrainIntoStage(composeResult, handle, sourceId, nowUs);
        if (rc != 0) throw new InvalidOperationException($"laplace_compose_drain_into_stage failed: {rc}");
    }

    private void ThrowIfDisposed()
    {
        if (IsClosed || IsInvalid) throw new ObjectDisposedException(nameof(IntentStage));
    }
}
