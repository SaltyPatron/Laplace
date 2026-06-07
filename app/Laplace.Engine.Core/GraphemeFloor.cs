namespace Laplace.Engine.Core;

/// <summary>
/// The shared codepoint→grapheme floor for a byte span — the same floor text (UAX#29) and
/// every grammar build over the same bytes, so a code identifier and a prose word reconcile
/// at the grapheme level. Owns the native floor handle + the tier tree (tier-0 codepoints +
/// tier-1 graphemes). The caller composes the tree's ids/coords via <c>HashComposer.Run</c> +
/// a codepoint resolver, then uses the byte-offset → grapheme mapping to attach grammar AST
/// nodes to contiguous grapheme runs.
/// </summary>
public sealed unsafe class GraphemeFloor : IDisposable
{
    /// <summary>Tier tree of codepoints (tier 0) + graphemes (tier 1). Ids/coords are unset
    /// until the caller runs <c>HashComposer.Run(Tree, resolver)</c>.</summary>
    public TierTree Tree { get; }

    public int CpN { get; }
    public int GraphFirstIdx { get; }
    public int GraphCount { get; }

    private IntPtr _floor;
    private readonly uint* _leafTextOff;
    private readonly uint* _leafTextLen;
    private readonly uint* _cpToGraph;

    private GraphemeFloor(TierTree tree, IntPtr floor)
    {
        Tree = tree;
        _floor = floor;
        CpN           = checked((int)NativeInterop.GraphemeFloorCpN(floor));
        GraphFirstIdx = checked((int)NativeInterop.GraphemeFloorGraphFirstIdx(floor));
        GraphCount    = checked((int)NativeInterop.GraphemeFloorGraphCount(floor));
        _leafTextOff  = NativeInterop.GraphemeFloorLeafTextOff(floor);
        _leafTextLen  = NativeInterop.GraphemeFloorLeafTextLen(floor);
        _cpToGraph    = NativeInterop.GraphemeFloorCpToGraph(floor);
    }

    /// <summary>Builds the floor for the given UTF-8 bytes (must be non-empty, valid UTF-8).</summary>
    public static GraphemeFloor Build(ReadOnlySpan<byte> utf8)
    {
        IntPtr treeHandle;
        IntPtr floor;
        fixed (byte* p = utf8)
        {
            floor = NativeInterop.GraphemeFloorBuildOwned(p, (nuint)utf8.Length, &treeHandle);
        }
        if (floor == IntPtr.Zero)
            throw new InvalidOperationException(
                "grapheme floor build failed (empty input / invalid UTF-8 / allocation failure)");
        return new GraphemeFloor(TierTree.FromExistingHandle(treeHandle), floor);
    }

    /// <summary>Byte offset of codepoint index <paramref name="cp"/> (0 ≤ cp &lt; CpN).</summary>
    public uint LeafByteOffset(int cp) => _leafTextOff[cp];

    /// <summary>Byte length of codepoint index <paramref name="cp"/>.</summary>
    public uint LeafByteLength(int cp) => _leafTextLen[cp];

    /// <summary>Grapheme ordinal (0 ≤ g &lt; GraphCount) containing codepoint <paramref name="cp"/>.</summary>
    public uint GraphemeOfCodepoint(int cp) => _cpToGraph[cp];

    /// <summary>Tier-tree node index of grapheme ordinal <paramref name="g"/>.</summary>
    public int GraphemeNodeIndex(int g) => GraphFirstIdx + g;

    /// <summary>
    /// Maps a half-open byte span [startByte, endByte) to the half-open range of grapheme
    /// ordinals [gStart, gEnd) it covers. Endpoints are snapped to grapheme boundaries.
    /// Returns false if the span is empty or out of range.
    /// </summary>
    public bool SpanToGraphemes(uint startByte, uint endByte, out int gStart, out int gEnd)
    {
        gStart = 0; gEnd = 0;
        if (endByte <= startByte || CpN == 0) return false;
        int cpStart = LowerBoundCp(startByte);
        int cpEnd = LowerBoundCp(endByte); // first cp at or after endByte
        if (cpStart >= CpN) return false;
        if (cpEnd <= cpStart) cpEnd = cpStart + 1;
        if (cpEnd > CpN) cpEnd = CpN;
        gStart = (int)_cpToGraph[cpStart];
        gEnd = (int)_cpToGraph[cpEnd - 1] + 1;
        return gEnd > gStart;
    }

    // First codepoint index whose byte offset is >= b (binary search over leaf_text_off, which is ascending).
    private int LowerBoundCp(uint b)
    {
        int lo = 0, hi = CpN;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_leafTextOff[mid] < b) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Dispose()
    {
        Tree?.Dispose();
        if (_floor != IntPtr.Zero)
        {
            NativeInterop.GraphemeFloorFreeOwned(_floor);
            _floor = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~GraphemeFloor() => Dispose();
}
