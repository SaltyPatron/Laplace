using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public sealed class TierTree : SafeHandle
{
    public const uint Invalid = uint.MaxValue;

    private TierTree(IntPtr handle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        lock (LaplaceCoreGate.Native)
            NativeInterop.TierTreeFree(handle);
        return true;
    }

    public static TierTree New(int capacityHint)
    {
        if (capacityHint < 0) throw new ArgumentOutOfRangeException(nameof(capacityHint));
        lock (LaplaceCoreGate.Native)
        {
            IntPtr handle = NativeInterop.TierTreeNew((nuint)capacityHint);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException("tier_tree_new returned NULL");
            return new TierTree(handle, ownsHandle: true);
        }
    }

    internal static TierTree FromExistingHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("handle is null", nameof(handle));
        return new TierTree(handle, ownsHandle: true);
    }

    public static TierTree FromBorrowedHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("handle is null", nameof(handle));
        return new TierTree(handle, ownsHandle: false);
    }

    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            lock (LaplaceCoreGate.Native)
                return checked((int)NativeInterop.TierTreeNodeCount(handle));
        }
    }

    public int Capacity
    {
        get
        {
            ThrowIfDisposed();
            lock (LaplaceCoreGate.Native)
                return checked((int)NativeInterop.TierTreeCapacity(handle));
        }
    }

    public uint AddLeaf(byte tier, uint atom, uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        lock (LaplaceCoreGate.Native)
        {
            uint idx = NativeInterop.TierTreeAddLeaf(handle, tier, atom, textRangeOff, textRangeLen);
            if (idx == Invalid) throw new InvalidOperationException("tier_tree_add_leaf failed (likely OOM)");
            return idx;
        }
    }

    public uint AddNode(byte tier, uint firstChildIdx, uint childCount,
                        uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        lock (LaplaceCoreGate.Native)
        {
            uint idx = NativeInterop.TierTreeAddNode(handle, tier, firstChildIdx, childCount,
                                                      textRangeOff, textRangeLen);
            if (idx == Invalid) throw new InvalidOperationException(
                "tier_tree_add_node rejected (invalid child range or OOM)");
            return idx;
        }
    }

    public void FinalizeParents()
    {
        ThrowIfDisposed();
        lock (LaplaceCoreGate.Native)
        {
            if (NativeInterop.TierTreeFinalize(handle) != 0)
                throw new InvalidOperationException("tier_tree_finalize failed");
        }
    }

    public TierNodeView GetNode(uint idx)
    {
        ThrowIfDisposed();
        lock (LaplaceCoreGate.Native)
        {
            TierNodeView view = default;
            unsafe
            {
                if (NativeInterop.TierTreeGetNode(handle, idx, &view) != 0)
                    throw new ArgumentOutOfRangeException(nameof(idx),
                        $"tier_tree_get_node({idx}) failed (count={NodeCount})");
            }
            return view;
        }
    }

    public uint CollapseIndex(uint idx)
    {
        ThrowIfDisposed();
        for (; ; )
        {
            var node = GetNode(idx);
            if (node.Tier <= 1 || node.ChildCount != 1) break;
            var child = GetNode(node.FirstChildIdx);
            if (child.TextRangeOff != node.TextRangeOff || child.TextRangeLen != node.TextRangeLen)
                break;
            idx = node.FirstChildIdx;
        }
        return idx;
    }

    public uint NaturalUnitIndex()
    {
        ThrowIfDisposed();
        int nc = NodeCount;
        if (nc <= 0) return 0;
        return CollapseIndex((uint)(nc - 1));
    }

    public bool ShouldEmitCompositional(uint idx)
    {
        ThrowIfDisposed();
        if (CollapseIndex(idx) != idx) return false;
        return GetNode(idx).Tier != 0;
    }

    public void SetId(uint idx, Hash128 id)
    {
        ThrowIfDisposed();
        unsafe
        {
            lock (LaplaceCoreGate.Native)
            {
                if (NativeInterop.TierTreeSetId(handle, idx, &id) != 0)
                    throw new ArgumentOutOfRangeException(nameof(idx));
            }
        }
    }

    public void SetCoord(uint idx, ReadOnlySpan<double> coord)
    {
        ThrowIfDisposed();
        if (coord.Length < 4) throw new ArgumentException("coord must have at least 4 elements", nameof(coord));
        unsafe
        {
            fixed (double* p = coord)
            {
                lock (LaplaceCoreGate.Native)
                {
                    if (NativeInterop.TierTreeSetCoord(handle, idx, p) != 0)
                        throw new ArgumentOutOfRangeException(nameof(idx));
                }
            }
        }
    }

    public void SetHilbert(uint idx, Hilbert128 hilbert)
    {
        ThrowIfDisposed();
        unsafe
        {
            lock (LaplaceCoreGate.Native)
            {
                if (NativeInterop.TierTreeSetHilbert(handle, idx, &hilbert) != 0)
                    throw new ArgumentOutOfRangeException(nameof(idx));
            }
        }
    }

    public IntPtr IdArrayPointer
    {
        get
        {
            ThrowIfDisposed();
            lock (LaplaceCoreGate.Native)
                return NativeInterop.TierTreeIdArray(handle);
        }
    }

    public Hash128[] NodeIds()
    {
        ThrowIfDisposed();
        lock (LaplaceCoreGate.Native)
        {
            int n = NodeCount;
            var ids = new Hash128[n];
            if (n == 0) return ids;
            IntPtr p = NativeInterop.TierTreeIdArray(handle);
            if (p == IntPtr.Zero) throw new InvalidOperationException("tier_tree_id_array returned NULL");
            unsafe
            {
                new ReadOnlySpan<Hash128>((void*)p, n).CopyTo(ids);
            }
            return ids;
        }
    }

    public IntPtr DangerousNativeHandle => handle;

    private void ThrowIfDisposed()
    {
        if (IsClosed || IsInvalid) throw new ObjectDisposedException(nameof(TierTree));
    }
}
