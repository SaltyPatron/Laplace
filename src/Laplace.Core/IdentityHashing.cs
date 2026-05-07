namespace Laplace.Core;

using System;
using System.Buffers;
using System.Collections.Generic;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native <c>BLAKE3HashService</c> kernel. Produces
/// the 32-byte BLAKE3-256 identity used everywhere in the substrate. There
/// is NO managed-side fallback hash and no parallel implementation — same
/// content, same hash, same row, no exceptions.
///
/// Phase 2 / Track D / D2.
/// </summary>
public sealed class IdentityHashing : IIdentityHashing
{
    public AtomId AtomId(ReadOnlySpan<byte> content)
    {
        var hash = new byte[NativeHash.HashBytes];
        unsafe
        {
            fixed (byte* contentPtr = content)
            fixed (byte* hashPtr = hash)
            {
                NativeHash.Atom(contentPtr, (nuint)content.Length, hashPtr);
            }
        }
        return new AtomId(hash);
    }

    public AtomId CompositionId(IReadOnlyList<AtomId> orderedChildren, IReadOnlyList<int> rleCounts)
    {
        if (orderedChildren.Count != rleCounts.Count)
        {
            throw new ArgumentException("orderedChildren and rleCounts must have equal length.");
        }
        var n = orderedChildren.Count;
        var packed = ArrayPool<byte>.Shared.Rent(n * NativeHash.HashBytes);
        var counts = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; ++i)
            {
                orderedChildren[i].AsSpan().CopyTo(packed.AsSpan(i * NativeHash.HashBytes, NativeHash.HashBytes));
                counts[i] = rleCounts[i];
            }
            var hash = new byte[NativeHash.HashBytes];
            unsafe
            {
                fixed (byte* packedPtr = packed)
                fixed (int*  countsPtr = counts)
                fixed (byte* hashPtr   = hash)
                {
                    NativeHash.Composition(packedPtr, countsPtr, (nuint)n, hashPtr);
                }
            }
            return new AtomId(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    public AtomId EdgeId(
        AtomId edgeType,
        IReadOnlyList<(AtomId Role, int RolePosition, AtomId Participant)> roleOrderedParticipants)
    {
        var n = roleOrderedParticipants.Count;
        var roleHashes        = ArrayPool<byte>.Shared.Rent(n * NativeHash.HashBytes);
        var participantHashes = ArrayPool<byte>.Shared.Rent(n * NativeHash.HashBytes);
        var rolePositions     = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; ++i)
            {
                var (role, pos, part) = roleOrderedParticipants[i];
                role.AsSpan().CopyTo(roleHashes.AsSpan(i * NativeHash.HashBytes, NativeHash.HashBytes));
                part.AsSpan().CopyTo(participantHashes.AsSpan(i * NativeHash.HashBytes, NativeHash.HashBytes));
                rolePositions[i] = pos;
            }
            var hash = new byte[NativeHash.HashBytes];
            unsafe
            {
                fixed (byte* edgeTypePtr = edgeType.AsSpan())
                fixed (byte* rolesPtr    = roleHashes)
                fixed (int*  posPtr      = rolePositions)
                fixed (byte* partsPtr    = participantHashes)
                fixed (byte* hashPtr     = hash)
                {
                    NativeHash.Edge(edgeTypePtr, rolesPtr, posPtr, partsPtr, (nuint)n, hashPtr);
                }
            }
            return new AtomId(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(roleHashes);
            ArrayPool<byte>.Shared.Return(participantHashes);
            ArrayPool<int>.Shared.Return(rolePositions);
        }
    }

    public AtomId[] AtomIdBatch(IReadOnlyList<ReadOnlyMemory<byte>> contents)
    {
        var n = contents.Count;
        var result = new AtomId[n];
        for (int i = 0; i < n; ++i)
        {
            result[i] = AtomId(contents[i].Span);
        }
        return result;
    }
}
