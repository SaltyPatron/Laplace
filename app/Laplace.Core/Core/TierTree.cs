namespace Laplace.Engine.Core;

public sealed unsafe class TierTree : IDisposable
{
    public const uint Invalid = uint.MaxValue;

    private readonly List<TierNodeView> _nodes;
    private bool _disposed;

    private TierTree(int capacityHint)
    {
        _nodes = new List<TierNodeView>(Math.Max(0, capacityHint));
    }

    public static TierTree New(int capacityHint = 0)
    {
        if (capacityHint < 0) throw new ArgumentOutOfRangeException(nameof(capacityHint));
        return new TierTree(capacityHint);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return _nodes.Count;
        }
    }

    public int Capacity
    {
        get
        {
            ThrowIfDisposed();
            return _nodes.Capacity;
        }
    }

    public long ResidentBytes
    {
        get
        {
            ThrowIfDisposed();
            return (long)_nodes.Capacity * sizeof(TierNodeView);
        }
    }

    public uint AddLeaf(byte tier, uint atom, uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        var node = new TierNodeView
        {
            Tier = tier,
            Role = 0,
            ParentIdx = Invalid,
            FirstChildIdx = Invalid,
            ChildCount = 0,
            TextRangeOff = textRangeOff,
            TextRangeLen = textRangeLen,
            Atom = atom,
        };
        _nodes.Add(node);
        return (uint)(_nodes.Count - 1);
    }

    public uint AddNode(byte tier, uint firstChildIdx, uint childCount,
                        uint textRangeOff, uint textRangeLen)
    {
        ThrowIfDisposed();
        if (childCount > 0 && (ulong)firstChildIdx + childCount > (ulong)_nodes.Count)
            throw new InvalidOperationException($"Child span [{firstChildIdx}..{(ulong)firstChildIdx + childCount}) exceeds node count {_nodes.Count}.");
        var node = new TierNodeView
        {
            Tier = tier,
            Role = 0,
            ParentIdx = Invalid,
            FirstChildIdx = firstChildIdx,
            ChildCount = childCount,
            TextRangeOff = textRangeOff,
            TextRangeLen = textRangeLen,
            Atom = 0,
        };
        _nodes.Add(node);
        return (uint)(_nodes.Count - 1);
    }

    internal void AppendPrebuilt(in TierNodeView node)
    {
        ThrowIfDisposed();
        _nodes.Add(node);
    }

    public void FinalizeParents()
    {
        ThrowIfDisposed();
        for (int i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i];
            if (n.ChildCount == 0 || n.FirstChildIdx == Invalid) continue;
            for (uint c = 0; c < n.ChildCount; c++)
            {
                int ci = checked((int)(n.FirstChildIdx + c));
                var child = _nodes[ci];
                child.ParentIdx = (uint)i;
                _nodes[ci] = child;
            }
        }
    }

    public TierNodeView GetNode(uint idx)
    {
        ThrowIfDisposed();
        if (idx >= (uint)_nodes.Count)
            throw new ArgumentOutOfRangeException(nameof(idx), $"Index {idx} out of range (count={_nodes.Count}).");
        return _nodes[(int)idx];
    }

    public uint CollapseIndex(uint idx)
    {
        ThrowIfDisposed();
        for (; ; )
        {
            var node = GetNode(idx);
            if (node.Tier == 0 || node.ChildCount != 1) break;
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
        int nc = _nodes.Count;
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
        if (idx >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(idx));
        var n = _nodes[(int)idx];
        n.Id = id;
        _nodes[(int)idx] = n;
    }

    public void SetCoord(uint idx, ReadOnlySpan<double> coord)
    {
        ThrowIfDisposed();
        if (idx >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(idx));
        if (coord.Length < 4) throw new ArgumentException("coord must have at least 4 elements", nameof(coord));
        var n = _nodes[(int)idx];
        n.Coord[0] = coord[0];
        n.Coord[1] = coord[1];
        n.Coord[2] = coord[2];
        n.Coord[3] = coord[3];
        _nodes[(int)idx] = n;
    }

    public void SetHilbert(uint idx, Hilbert128 hilbert)
    {
        ThrowIfDisposed();
        if (idx >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(idx));
        var n = _nodes[(int)idx];
        n.Hilbert = hilbert;
        _nodes[(int)idx] = n;
    }

    public Hash128[] NodeIds()
    {
        ThrowIfDisposed();
        var ids = new Hash128[_nodes.Count];
        for (int i = 0; i < _nodes.Count; i++)
            ids[i] = _nodes[i].Id;
        return ids;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
