using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Managed wrapper over the engine <c>tier_tree_t*</c> opaque handle
/// (engine/core/include/laplace/core/tier_tree.h). RAII via
/// <see cref="SafeHandle"/>; engine memory freed on dispose.
///
/// Construction order matches the engine invariant: leaves first, then
/// interior nodes referencing already-added child ranges, then root last.
/// <see cref="Finalize"/> must be called once after construction to
/// populate <see cref="TierNodeView.ParentIdx"/>; after that the tree is
/// read-only via the engine ABI (id/coord/hilbert slots stay writable
/// until <see cref="HashComposer"/> populates them).
/// </summary>
public sealed class TierTree : SafeHandle
{
    /// <summary>Sentinel for unset parent / first-child indices —
    /// matches engine <c>TIER_TREE_INVALID</c> (<c>UINT32_MAX</c>).</summary>
    public const uint Invalid = uint.MaxValue;

    private TierTree(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeInterop.TierTreeFree(handle);
        return true;
    }

    /// <summary>Allocate a new tree with the given capacity hint (number
    /// of expected nodes). Hint is non-binding — the arena grows past it
    /// geometrically when add operations exceed it. Throws if engine
    /// allocation fails.</summary>
    public static TierTree New(int capacityHint)
    {
        if (capacityHint < 0) throw new ArgumentOutOfRangeException(nameof(capacityHint));
        IntPtr handle = NativeInterop.TierTreeNew((nuint)capacityHint);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("tier_tree_new returned NULL");
        return new TierTree(handle);
    }

    /// <summary>Wrap a tier_tree_t* that was allocated by an engine call
    /// (e.g. <see cref="TextDecomposer.Run"/>). Ownership transfers to
    /// the returned <see cref="TierTree"/>; caller MUST NOT call
    /// <c>tier_tree_free</c> on the handle.</summary>
    internal static TierTree FromExistingHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("handle is null", nameof(handle));
        return new TierTree(handle);
    }

    /// <summary>Number of nodes currently in the tree.</summary>
    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return checked((int)NativeInterop.TierTreeNodeCount(handle));
        }
    }

    /// <summary>Current internal arena capacity (≥ <see cref="NodeCount"/>).</summary>
    public int Capacity
    {
        get
        {
            ThrowIfDisposed();
            return checked((int)NativeInterop.TierTreeCapacity(handle));
        }
    }

    /// <summary>Add a leaf node (T0 atom). Returns the new node's index.
    /// Throws on engine OOM.</summary>
    public uint AddLeaf(byte tier, uint atom, uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        uint idx = NativeInterop.TierTreeAddLeaf(handle, tier, atom, textRangeOff, textRangeLen);
        if (idx == Invalid) throw new InvalidOperationException("tier_tree_add_leaf failed (likely OOM)");
        return idx;
    }

    /// <summary>Add an interior node referencing a contiguous range of
    /// previously-added children. Pass <see cref="Invalid"/> +
    /// <paramref name="childCount"/>=0 for an empty interior node.</summary>
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

    /// <summary>Single-pass populate <see cref="TierNodeView.ParentIdx"/>
    /// for every node. Must be called once after construction; idempotent.
    /// (Named <c>FinalizeParents</c> to avoid shadowing
    /// <c>System.Object.Finalize</c>.)</summary>
    public void FinalizeParents()
    {
        ThrowIfDisposed();
        if (NativeInterop.TierTreeFinalize(handle) != 0)
            throw new InvalidOperationException("tier_tree_finalize failed");
    }

    /// <summary>Copy node fields at <paramref name="idx"/> into a fresh
    /// <see cref="TierNodeView"/>. Throws on out-of-bounds.</summary>
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

    /// <summary>
    /// Index of the entity's "natural unit" node. The document root is built
    /// last (index <see cref="NodeCount"/>−1), but a lone word or sentence is
    /// wrapped in redundant single-child sentence/document tiers. This descends
    /// from the document root through single-child wrapper tiers, stopping at the
    /// word tier (2): a bare word ("cat") binds at its word node — not the
    /// wrapping sentence/document — while a real multi-word sentence stays a
    /// sentence and a multi-sentence document stays a document. Returns 0 for an
    /// empty tree. This is the unit a decomposer content-addresses + attests on
    /// (fixes the §9.3 "everything binds to the tier-4 document root" defect).
    /// </summary>
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

    /// <summary>Set the BLAKE3-128 id of node <paramref name="idx"/>.
    /// Used by <see cref="HashComposer"/> + tests; not normally called by
    /// decomposer code.</summary>
    public void SetId(uint idx, Hash128 id)
    {
        ThrowIfDisposed();
        unsafe
        {
            if (NativeInterop.TierTreeSetId(handle, idx, &id) != 0)
                throw new ArgumentOutOfRangeException(nameof(idx));
        }
    }

    /// <summary>Set the 4D coord of node <paramref name="idx"/>.</summary>
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

    /// <summary>Set the Hilbert index of node <paramref name="idx"/>.</summary>
    public void SetHilbert(uint idx, Hilbert128 hilbert)
    {
        ThrowIfDisposed();
        unsafe
        {
            if (NativeInterop.TierTreeSetHilbert(handle, idx, &hilbert) != 0)
                throw new ArgumentOutOfRangeException(nameof(idx));
        }
    }

    /// <summary>Direct pointer to the contiguous id array for hot-path
    /// consumers (HashComposer, MerkleDedup batch primitives). Valid only
    /// until the next add operation or dispose. Use with extreme care —
    /// the engine ABI guarantees zero-copy SoA layout.</summary>
    public IntPtr IdArrayPointer
    {
        get { ThrowIfDisposed(); return NativeInterop.TierTreeIdArray(handle); }
    }

    /// <summary>The opaque native pointer — for passing to native APIs that
    /// expect <c>tier_tree_t*</c>. Use under a <c>using</c> scope or with
    /// an explicit <see cref="DangerousAddRef"/> / <see cref="DangerousRelease"/>
    /// pair.</summary>
    public IntPtr DangerousNativeHandle => handle;

    private void ThrowIfDisposed()
    {
        if (IsClosed || IsInvalid) throw new ObjectDisposedException(nameof(TierTree));
    }
}
