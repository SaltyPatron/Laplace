namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Run-length encoding kernel. Used at every composition tier so entities are
/// referenced as FEW times as physically possible. The native
/// <c>RleService</c> exposes encode / decode / pattern-match over byte spans
/// and over hash arrays (composition_child rows carry rle_count for each
/// adjacent identical child).
/// </summary>
public interface IRleEncoder
{
    /// <summary>RLE-encode an ordered hash sequence into (hash, count) runs.</summary>
    (AtomId Hash, int Count)[] EncodeHashes(ReadOnlySpan<AtomId> orderedChildren);

    /// <summary>Inverse of EncodeHashes.</summary>
    AtomId[] DecodeHashes(ReadOnlySpan<(AtomId Hash, int Count)> runs);

    /// <summary>Encode a byte span (used for content-level RLE during atom seeding).</summary>
    (byte Value, int Count)[] EncodeBytes(ReadOnlySpan<byte> bytes);
}
