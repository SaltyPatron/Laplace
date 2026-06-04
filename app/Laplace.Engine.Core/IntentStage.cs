using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Engine.Core;

/// <summary>
/// Which substrate table a row / emitted COPY stream targets. Matches
/// engine <c>intent_stage_table_t</c>.
/// </summary>
public enum IntentStageTable
{
    Entities      = 1,
    Physicalities = 2,
    Attestations  = 3,
}

/// <summary>
/// Managed wrapper over the engine <c>intent_stage_t*</c> opaque handle
/// (engine/core/include/laplace/core/intent_stage.h). RAII via
/// <see cref="SafeHandle"/>.
///
/// Used by <c>Laplace.SubstrateCRUD.NpgsqlSubstrateWriter</c>
/// to materialize PG COPY BINARY byte streams in-engine — the C# layer is a
/// thin transport over engine-emitted bytes. Zero per-row managed
/// allocations. One COPY round-trip per table.
/// </summary>
public sealed class IntentStage : SafeHandle
{
    /// <summary>PG epoch (2000-01-01 UTC) as microseconds since the Unix
    /// epoch — caller-side helper for converting Unix-µs timestamps to the
    /// values <see cref="AddAttestation"/> + <see cref="AddPhysicality"/>
    /// expect.</summary>
    public const long PgEpochUnixUs = 946684800000000L;

    private IntentStage(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeInterop.IntentStageFree(handle);
        return true;
    }

    /// <summary>Allocate an intent stage with a row-count hint that
    /// pre-sizes the per-table buffers. Hint is non-binding — buffers
    /// grow geometrically past it.</summary>
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

    /// <summary>Comma-separated column list matching the wire bytes — for
    /// composing the <c>COPY laplace.entities (…) FROM STDIN BINARY</c>
    /// statement before <see cref="EmitCopyBinary"/>.</summary>
    public static string CopyColumnList(IntentStageTable table)
    {
        IntPtr p = NativeInterop.IntentStageCopyColumnList((int)table);
        return Marshal.PtrToStringUTF8(p)
            ?? throw new ArgumentOutOfRangeException(nameof(table));
    }

    /// <summary>Add one <c>entities</c> row.</summary>
    /// <param name="firstObservedBy">Nullable; pass <c>null</c> for SQL NULL.</param>
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

 /// <summary>Add one <c>physicalities</c> row. See schema
    /// shape; pass <paramref name="trajectoryXyzm"/>=<c>null</c> for SQL
    /// NULL trajectory.</summary>
    public void AddPhysicality(
        Hash128         id,
        Hash128         entityId,
        Hash128         sourceId,
        short           kind,
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
                    handle, &id, &entityId, &sourceId, kind, pCoord, &hilbertIndex,
                    nVerts == 0 ? null : pTraj, nVerts, nConstituents,
                    arNull, arVal, sdNull, sdVal, observedAtUnixUs);
                if (rc != 0) throw new InvalidOperationException("intent_stage_add_physicality failed");
            }
        }
    }

    /// <summary>Add one <c>attestations</c> (EVIDENCE) row — PROVENANCE, never
    /// values: who witnessed which relation, when, how many games, and the
    /// dissent record as a CLASS (<paramref name="outcome"/>: 0=refute, 1=draw,
    /// 2=confirm — never a magnitude). The witness's value is testimony,
    /// consumed into the consensus accumulation at ingest and not persisted;
    /// the accumulated rating/rd/volatility live on consensus.</summary>
    public void AddAttestation(
        Hash128  id,
        Hash128  subjectId,
        Hash128  kindId,
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
                handle, &id, &subjectId, &kindId, objPtr, &sourceId, ctxPtr,
                outcome, lastObservedAtUnixUs, observationCount);
            if (rc != 0) throw new InvalidOperationException("intent_stage_add_attestation failed");
        }
    }

    /// <summary>Emit the complete COPY BINARY stream for <paramref name="table"/>
    /// (header + accumulated tuples + trailer) into a fresh byte array.
    /// For zero-copy emission, use the <see cref="EmitCopyBinary(IntentStageTable, Span{byte})"/>
    /// overload with a caller-allocated buffer.</summary>
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

    /// <summary>The raw COPY-binary TUPLE bytes for <paramref name="table"/> (no header/
    /// trailer) and their length, owned by the engine stage — for STREAMING the native
    /// serialization straight into a COPY socket instead of materializing a managed array.
    /// Valid until the next add / dispose.</summary>
    public unsafe (IntPtr Ptr, long Len) TupleBuffer(IntentStageTable table)
    {
        ThrowIfDisposed();
        nuint len;
        byte* p = NativeInterop.IntentStageTuplePtr(handle, (int)table, &len);
        return ((IntPtr)p, checked((long)len));
    }

    /// <summary>Emit into the caller-allocated <paramref name="dest"/>.
    /// Returns the number of bytes required (also written if dest was
    /// large enough; nothing written otherwise).</summary>
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

    private void ThrowIfDisposed()
    {
        if (IsClosed || IsInvalid) throw new ObjectDisposedException(nameof(IntentStage));
    }
}
