using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>Borrowed batch input matching physicality_descriptor_input_t.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PhysicalityDescriptorInputNative
{
    public Hash128 EntityId;
    public short Type;
    public fixed double Coordinate[4];
    public Hilbert128 HilbertIndex;
    public double* Trajectory;
    public nuint TrajectoryVertices;
    public int Constituents;
    public int AlignmentResidualIsNull;
    public double AlignmentResidual;
    public int SourceDimIsNull;
    public int SourceDim;
}

public sealed partial class IntentStage
{
    public static IntentStage NewBounded(int rowCapacityHint, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCapacityHint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        lock (LaplaceCoreGate.Native)
        {
            IntPtr stage = NativeInterop.IntentStageNewBounded(
                (nuint)rowCapacityHint, checked((nuint)maximumBytes));
            if (stage == IntPtr.Zero)
                throw new OutOfMemoryException("native bounded intent stage exceeded its allocation grant");
            return new IntentStage(stage);
        }
    }

    public long AllocatedBytes
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                ThrowIfDisposed();
                long bytes = checked((long)NativeInterop.IntentStageMemoryBytes(handle));
                GC.KeepAlive(this);
                return bytes;
            }
        }
    }

    /// <summary>Import the native tuple transport through its native framing owner.</summary>
    public static unsafe IntentStage FromTupleBytes(
        ReadOnlySpan<byte> entities, ReadOnlySpan<byte> physicalities,
        ReadOnlySpan<byte> attestations, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        lock (LaplaceCoreGate.Native)
        fixed (byte* e = entities)
        fixed (byte* p = physicalities)
        fixed (byte* a = attestations)
        {
            IntPtr stage = IntPtr.Zero;
            int status = NativeInterop.IntentStageFromTupleBytes(
                e, (nuint)entities.Length, p, (nuint)physicalities.Length,
                a, (nuint)attestations.Length, checked((nuint)maximumBytes), &stage);
            if (status != 0 || stage == IntPtr.Zero)
            {
                if (stage != IntPtr.Zero) NativeInterop.IntentStageFree(stage);
                throw new InvalidOperationException($"native intent tuple import failed: {status}");
            }
            return new IntentStage(stage);
        }
    }

    /// <summary>
    /// Stage the complete borrowed physicality batch in one native call. The caller
    /// pins every trajectory for this call and discards the stage if a row fails.
    /// </summary>
    public unsafe void AddPhysicalityBatch(
        ReadOnlySpan<PhysicalityDescriptorInputNative> inputs,
        ReadOnlySpan<Hash128> declaredPhysicalityIds,
        ReadOnlySpan<long> observedAtUnixUs)
    {
        if (inputs.Length != observedAtUnixUs.Length || inputs.Length != declaredPhysicalityIds.Length)
            throw new ArgumentException("physicality inputs and observation timestamps must align");
        lock (LaplaceCoreGate.Native)
        {
            ThrowIfDisposed();
            fixed (PhysicalityDescriptorInputNative* rows = inputs)
            fixed (Hash128* ids = declaredPhysicalityIds)
            fixed (long* times = observedAtUnixUs)
            {
                int status = NativeInterop.PhysicalityDescriptorStageAddBatch(
                    handle, rows, ids, times, (nuint)inputs.Length);
                GC.KeepAlive(this);
                if (status != 0)
                    throw new InvalidOperationException($"native physicality batch staging failed: {status}");
            }
        }
    }
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "intent_stage_new_bounded")]
    internal static partial IntPtr IntentStageNewBounded(nuint rowCapacityHint, nuint maximumBytes);

    [LibraryImport(Library, EntryPoint = "intent_stage_memory_bytes")]
    internal static partial nuint IntentStageMemoryBytes(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_from_tuple_bytes")]
    internal static partial int IntentStageFromTupleBytes(
        byte* entities, nuint entityBytes, byte* physicalities, nuint physicalityBytes,
        byte* attestations, nuint attestationBytes, nuint maximumBytes, IntPtr* stage);

    [LibraryImport(Library, EntryPoint = "physicality_descriptor_stage_add_batch")]
    internal static partial int PhysicalityDescriptorStageAddBatch(
        IntPtr stage, PhysicalityDescriptorInputNative* inputs, Hash128* declaredPhysicalityIds, long* timestamps, nuint count);
}
