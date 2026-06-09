using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public sealed class TierTree : SafeHandle
{
    public const uint Invalid = uint.MaxValue;

    private TierTree(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeInterop.TierTreeFree(handle);
        return true;
    }

    public static TierTree New(int capacityHint)
    {
        if (capacityHint < 0) throw new ArgumentOutOfRangeException(nameof(capacityHint));
        IntPtr handle = NativeInterop.TierTreeNew((nuint)capacityHint);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("tier_tree_new returned NULL");
        return new TierTree(handle);
    }

    internal static TierTree FromExistingHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("handle is null", nameof(handle));
        return new TierTree(handle);
    }

    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return checked((int)NativeInterop.TierTreeNodeCount(handle));
        }
    }

    public int Capacity
    {
        get
        {
            ThrowIfDisposed();
            return checked((int)NativeInterop.TierTreeCapacity(handle));
        }
    }

    public uint AddLeaf(byte tier, uint atom, uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        uint idx = NativeInterop.TierTreeAddLeaf(handle, tier, atom, textRangeOff, textRangeLen);
        if (idx == Invalid) throw new InvalidOperationException("tier_tree_add_leaf failed (likely OOM)");
        return idx;
    }

    public uint AddNode(byte tier, uint firstChildIdx, uint childCount,
                        uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        uint idx = NativeInterop.TierTreeAddNode(handle, tier, firstChildIdx, childCount,
                                                  textRangeOff, textRangeLen);
        if (idx == Invalid) throw new InvalidOperationException(
            "tier_tree_add_node rejected (invalid child range or OOM)");
        return idx;
    }

    public void FinalizeParents()
    {
        ThrowIfDisposed();
        if (NativeInterop.TierTreeFinalize(handle) != 0)
            throw new InvalidOperationException("tier_tree_finalize failed");
    }

    public TierNodeView GetNode(uint idx)
    {
        ThrowIfDisposed();
        TierNodeView view = default;
        unsafe
        {
            if (NativeInterop.TierTreeGetNode(handle, idx, &view) != 0)
                throw new ArgumentOutOfRangeException(nameof(idx),
                    $"tier_tree_get_node({idx}) failed (count={NodeCount})");
        }
        return view;
    }

    public uint NaturalUnitIndex()
    {
        ThrowIfDisposed();
        int nc = NodeCount;
        if (nc <= 0) return 0;
        uint idx = (uint)(nc - 1);
        TierNodeView node = GetNode(idx);
        while (node.Tier > 2 && node.ChildCount == 1)
        {
            idx  = node.FirstChildIdx;
            node = GetNode(idx);
        }
        return idx;
    }

    /// <summary>
    /// True when a compositional node should be emitted: the natural unit itself, or a node whose
    /// text span differs from the natural unit (real multi-unit structure). Unary wrappers sharing
    /// the natural unit's span are suppressed.
    /// </summary>
    public bool ShouldEmitCompositional(uint idx)
    {
        ThrowIfDisposed();
        uint naturalIdx = NaturalUnitIndex();
        if (idx == naturalIdx) return true;

        var node    = GetNode(idx);
        var natural = GetNode(naturalIdx);
        if (node.TextRangeOff != natural.TextRangeOff || node.TextRangeLen != natural.TextRangeLen)
            return true;

        return !IsUnaryAncestorOf(idx, naturalIdx);
    }

    private bool IsUnaryAncestorOf(uint ancestor, uint descendant)
    {
        uint cur = (uint)(NodeCount - 1);
        while (cur != descendant)
        {
            if (cur == ancestor) return true;
            var n = GetNode(cur);
            if (n.ChildCount != 1) return false;
            cur = n.FirstChildIdx;
        }
        return false;
    }

    public void SetId(uint idx, Hash128 id)
    {
        ThrowIfDisposed();
        unsafe
        {
            if (NativeInterop.TierTreeSetId(handle, idx, &id) != 0)
                throw new ArgumentOutOfRangeException(nameof(idx));
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
                if (NativeInterop.TierTreeSetCoord(handle, idx, p) != 0)
                    throw new ArgumentOutOfRangeException(nameof(idx));
            }
        }
    }

    public void SetHilbert(uint idx, Hilbert128 hilbert)
    {
        ThrowIfDisposed();
        unsafe
        {
            if (NativeInterop.TierTreeSetHilbert(handle, idx, &hilbert) != 0)
                throw new ArgumentOutOfRangeException(nameof(idx));
        }
    }

    public IntPtr IdArrayPointer
    {
        get { ThrowIfDisposed(); return NativeInterop.TierTreeIdArray(handle); }
    }

    public IntPtr DangerousNativeHandle => handle;

    private void ThrowIfDisposed()
    {
        if (IsClosed || IsInvalid) throw new ObjectDisposedException(nameof(TierTree));
    }
}
