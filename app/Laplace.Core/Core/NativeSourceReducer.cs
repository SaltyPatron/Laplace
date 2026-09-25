using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public enum SourceReduceStream
{
    Entities = 1,
    Physicalities = 2,
    Attestations = 3,
    CellKeys = 4,
    Consensus = 5,
    Masks = 6,
    Standing = 7,
}

[StructLayout(LayoutKind.Sequential)]
public struct SourceReduceStats
{
    public ulong Entities;
    public ulong Physicalities;
    public ulong Attestations;
    public ulong StagedEntities;
    public ulong StagedPhysicalities;
    public ulong StagedAttestations;
    public ulong MergedAttestations;
    public ulong UnplacedEntities;
    public ulong Observations;
    public ulong Cells;
    public ulong Bytes;
    public ulong MaskedEntities;
}

/// <summary>
/// One source's native reduction (engine/core source_reduce.cpp): absorbs intent stages,
/// reduces rows by identity, derives entity Highway masks, routes rows to lanes by their
/// PostgreSQL HASH partition as sorted binary COPY streams, and folds one Glicko-2 rating
/// period per witness per cell from prior standing. No row crosses into managed memory;
/// the caller moves the native COPY streams to PostgreSQL. One owner; calls are
/// serialized on the native gate.
/// </summary>
public sealed class NativeSourceReducer : SafeHandle
{
    private NativeSourceReducer(IntPtr pointer) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(pointer);

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static NativeSourceReducer Create()
    {
        lock (LaplaceCoreGate.Native)
        {
            IntPtr pointer = NativeInterop.SourceReduceNew();
            return pointer == IntPtr.Zero
                ? throw new OutOfMemoryException("laplace_source_reduce_new returned NULL")
                : new NativeSourceReducer(pointer);
        }
    }

    /// <summary>Reads the stage's rows; the caller keeps ownership of the stage.</summary>
    public void Add(IntentStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        bool added = false;
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            try
            {
                stage.DangerousAddRef(ref added);
                Check(NativeInterop.SourceReduceAddStage(handle, stage.DangerousGetHandle()));
            }
            finally
            {
                if (added) stage.DangerousRelease();
            }
        }
    }

    public unsafe void ExcludeFromFold(ReadOnlySpan<Hash128> typeIds)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            fixed (Hash128* types = typeIds)
                Check(NativeInterop.SourceReduceExcludeFoldTypes(handle, types, (nuint)typeIds.Length));
        }
    }

    /// <summary>HASH partition moduli of the entity, physicality and claim tables (0 when a
    /// table is not HASH partitioned on its routing key): lanes then own whole partitions.</summary>
    public void Partitions(int entityModulus, int physicalityModulus, int claimModulus)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            Check(NativeInterop.SourceReducePartitions(handle,
                (uint)entityModulus, (uint)physicalityModulus, (uint)claimModulus));
        }
    }

    public void Route(int lanes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lanes);
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            Check(NativeInterop.SourceReduceRoute(handle, (uint)lanes));
        }
    }

    public unsafe SourceReduceStats Stats
    {
        get
        {
            lock (LaplaceCoreGate.Native)
            {
                ObjectDisposedException.ThrowIf(IsClosed, this);
                SourceReduceStats stats = default;
                NativeInterop.SourceReduceStats(handle, &stats);
                return stats;
            }
        }
    }

    /// <summary>
    /// One lane's binary COPY stream (header, rows, trailer). The bytes are native memory
    /// owned by the reducer: valid while the reducer lives and until the same stream of the
    /// same lane is rebuilt.
    /// </summary>
    public unsafe (IntPtr Bytes, long Length, ulong Rows) Stream(SourceReduceStream stream, int lane)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            nuint length;
            ulong rows;
            byte* bytes = NativeInterop.SourceReduceStream(handle, (int)stream, (uint)lane, &length, &rows);
            if (bytes == null) throw new InvalidOperationException(Error());
            return ((IntPtr)bytes, checked((long)length), rows);
        }
    }

    /// <summary>Restricts the lane's fold to the claims PostgreSQL admitted (a binary COPY
    /// stream of their ids). An empty span admits every claim of the lane.</summary>
    public unsafe void Admit(int lane, ReadOnlySpan<byte> novelCopy)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            fixed (byte* bytes = novelCopy)
                Check(NativeInterop.SourceReduceAdmit(handle, (uint)lane, bytes, (nuint)novelCopy.Length));
        }
    }

    public unsafe ulong BuildCells(int lane)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            ulong cells = 0;
            Check(NativeInterop.SourceReduceCells(handle, (uint)lane, &cells));
            return cells;
        }
    }

    public unsafe void Fold(int lane, ReadOnlySpan<byte> priorCopy)
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            fixed (byte* bytes = priorCopy)
                Check(NativeInterop.SourceReduceFold(handle, (uint)lane, bytes, (nuint)priorCopy.Length));
        }
    }

    /// <summary>Claimed (entity, relation type) pairs whose relation has no Highway bit,
    /// for the dynamic-family resolver. Valid after <see cref="Route"/>.</summary>
    public unsafe (Hash128 Entity, Hash128 Type)[] UnresolvedMaskPairs()
    {
        lock (LaplaceCoreGate.Native)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            Hash128* pairs = null;
            nuint count = 0;
            Check(NativeInterop.SourceReduceMasks(handle, &pairs, &count));
            var result = new (Hash128, Hash128)[checked((int)count)];
            for (int i = 0; i < result.Length; i++)
                result[i] = (pairs[2 * i], pairs[2 * i + 1]);
            return result;
        }
    }

    protected override bool ReleaseHandle()
    {
        lock (LaplaceCoreGate.Native) NativeInterop.SourceReduceFree(handle);
        return true;
    }

    private void Check(int result)
    {
        if (result == -2) throw new OutOfMemoryException(Error());
        if (result != 0) throw new InvalidDataException(Error());
    }

    private string Error()
    {
        string? message = Marshal.PtrToStringUTF8(NativeInterop.SourceReduceError(handle));
        return string.IsNullOrEmpty(message) ? "Native source reduction failed." : message;
    }
}

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_new")]
    internal static partial IntPtr SourceReduceNew();
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_free")]
    internal static partial void SourceReduceFree(IntPtr reducer);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_error")]
    internal static partial IntPtr SourceReduceError(IntPtr reducer);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_add_stage")]
    internal static partial int SourceReduceAddStage(IntPtr reducer, IntPtr stage);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_exclude_fold_types")]
    internal static partial int SourceReduceExcludeFoldTypes(IntPtr reducer, Hash128* types, nuint count);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_partitions")]
    internal static partial int SourceReducePartitions(IntPtr reducer, uint entity, uint physicality, uint claim);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_route")]
    internal static partial int SourceReduceRoute(IntPtr reducer, uint lanes);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_stats")]
    internal static partial void SourceReduceStats(IntPtr reducer, SourceReduceStats* stats);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_stream")]
    internal static partial byte* SourceReduceStream(IntPtr reducer, int stream, uint lane, nuint* length, ulong* rows);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_admit")]
    internal static partial int SourceReduceAdmit(IntPtr reducer, uint lane, byte* novel, nuint length);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_cells")]
    internal static partial int SourceReduceCells(IntPtr reducer, uint lane, ulong* cells);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_fold")]
    internal static partial int SourceReduceFold(IntPtr reducer, uint lane, byte* priors, nuint length);
    [LibraryImport(Library, EntryPoint = "laplace_source_reduce_masks")]
    internal static partial int SourceReduceMasks(IntPtr reducer, Hash128** pairs, nuint* count);
}
