using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tatoeba;

/// <summary>
/// Transient Tatoeba-row-id → CONTENT ROOT map, built once at initialize and discarded
/// with the run.
///
/// WHY THIS EXISTS: links.csv references sentences by Tatoeba's row number, so the link
/// lane has to turn an integer into the sentence it names. The previous answer was to mint
/// a `tatoeba/sentence/{id}` ENTITY per referenced id and attest IS_TRANSLATION_OF between
/// those — source-keyed identity, which is the entity-resolution table content addressing
/// exists to abolish, and measured at ~1.56 entity rows per link (the single largest row
/// category during the link phase). The id is SCAFFOLDING: it exists only because the links
/// file cannot inline the text. It is not knowledge, so it gets no entity, no geometry, and
/// no trajectory — it gets resolved and thrown away.
///
/// NOT a stored index and NOT a trajectory. Tatoeba's row order is an artifact of their
/// database; recording it would attest a fact about their file layout, not about language.
///
/// Chunked flat array rather than a Dictionary: ids are dense (MEASURED 13,262,153 rows
/// spanning ids 1..13,730,510 = 96.6% occupancy), so direct indexing costs 16 B/slot
/// (~220 MB at present corpus size) against roughly 3x that for a hashtable, with no
/// rehash and O(1) lookup. Writes happen once per id from the parallel build; reads happen
/// only afterwards.
/// </summary>
internal sealed class TatoebaIdMap
{
    private const int ChunkBits = 20;                 // 1,048,576 ids per chunk = 16 MiB
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    private readonly object _grow = new();
    private volatile Hash128[]?[] _chunks = new Hash128[]?[64];
    private long _count;

    /// <summary>Resolved sentence ids held.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>
    /// Default(Hash128) is the ABSENT sentinel — an all-zero BLAKE3 root is not reachable
    /// for any real sentence, so no valid root is mistaken for a miss.
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
