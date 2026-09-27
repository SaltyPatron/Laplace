using Laplace.Engine.Core;
using System.Numerics;

namespace Laplace.Decomposers.Tatoeba;

/// <summary>
/// Transient Tatoeba row id → content root map, discarded with the run. links.csv names
/// sentences by row id; the id is packaging, so it is resolved to the sentence's content
/// root and gets no entity, geometry or trajectory. The row order is not recorded either:
/// it describes the source database's layout, not language.
///
/// A chunked flat array rather than a dictionary: row ids are dense, so direct indexing
/// costs 16 bytes per slot with O(1) lookup and no rehash. Each id is written once by the
/// parallel sentence pass; reads happen only afterwards.
/// </summary>
internal sealed class TatoebaIdMap
{
    private static readonly int ChunkBits = BitOperations.Log2((uint)Math.Max(1, Math.Min(
        Array.MaxLength,
        IngestSizing.ResolveApplyIo(
            IngestTopology.Current.ApplyPartitions).CacheBytesPerOwner
            / MemoryTopology.Hash128Bytes)));
    private static readonly int ChunkSize = 1 << ChunkBits;
    private static readonly int ChunkMask = ChunkSize - 1;

    private readonly object _grow = new();
    private volatile Hash128[]?[] _chunks = new Hash128[]?[1];
    private long _count;

    /// <summary>Resolved sentence ids held.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>
    /// Default(Hash128) marks an absent slot; no real sentence root is all-zero, so no
    /// valid root reads as a miss.
    /// </summary>
    public void Set(long id, Hash128 root)
    {
        if (id < 0) return;
        int chunkIx = (int)(id >> ChunkBits);
        var chunk = EnsureChunk(chunkIx);
        int slot = (int)(id & ChunkMask);
        if (chunk[slot].Equals(default(Hash128)))
            Interlocked.Increment(ref _count);
        chunk[slot] = root;
    }

    public bool TryGet(long id, out Hash128 root)
    {
        root = default;
        if (id < 0) return false;
        int chunkIx = (int)(id >> ChunkBits);
        var chunks = _chunks;
        if (chunkIx >= chunks.Length) return false;
        var chunk = chunks[chunkIx];
        if (chunk is null) return false;
        root = chunk[(int)(id & ChunkMask)];
        return !root.Equals(default(Hash128));
    }

    private Hash128[] EnsureChunk(int chunkIx)
    {
        var chunks = _chunks;
        if (chunkIx < chunks.Length && chunks[chunkIx] is { } existing)
            return existing;

        lock (_grow)
        {
            chunks = _chunks;
            if (chunkIx >= chunks.Length)
            {
                int len = chunks.Length;
                while (len <= chunkIx) len <<= 1;
                var grown = new Hash128[]?[len];
                Array.Copy(chunks, grown, chunks.Length);
                _chunks = chunks = grown;
            }
            return chunks[chunkIx] ??= new Hash128[ChunkSize];
        }
    }
}
