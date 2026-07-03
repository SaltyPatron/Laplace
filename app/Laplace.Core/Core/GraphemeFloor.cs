namespace Laplace.Engine.Core;









public sealed unsafe class GraphemeFloor : IDisposable
{


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
        CpN = checked((int)NativeInterop.GraphemeFloorCpN(floor));
        GraphFirstIdx = checked((int)NativeInterop.GraphemeFloorGraphFirstIdx(floor));
        GraphCount = checked((int)NativeInterop.GraphemeFloorGraphCount(floor));
        _leafTextOff = NativeInterop.GraphemeFloorLeafTextOff(floor);
        _leafTextLen = NativeInterop.GraphemeFloorLeafTextLen(floor);
        _cpToGraph = NativeInterop.GraphemeFloorCpToGraph(floor);
    }


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


    public uint LeafByteOffset(int cp) => _leafTextOff[cp];


    public uint LeafByteLength(int cp) => _leafTextLen[cp];


    public uint GraphemeOfCodepoint(int cp) => _cpToGraph[cp];


    public int GraphemeNodeIndex(int g) => GraphFirstIdx + g;






    public bool SpanToGraphemes(uint startByte, uint endByte, out int gStart, out int gEnd)
    {
        gStart = 0; gEnd = 0;
        if (endByte <= startByte || CpN == 0) return false;
        int cpStart = LowerBoundCp(startByte);
        int cpEnd = LowerBoundCp(endByte);
        if (cpStart >= CpN) return false;
        if (cpEnd <= cpStart) cpEnd = cpStart + 1;
        if (cpEnd > CpN) cpEnd = CpN;
        gStart = (int)_cpToGraph[cpStart];
        gEnd = (int)_cpToGraph[cpEnd - 1] + 1;
        return gEnd > gStart;
    }


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
