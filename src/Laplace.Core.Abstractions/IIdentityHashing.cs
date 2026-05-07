namespace Laplace.Core.Abstractions;

using System;
using System.Collections.Generic;

/// <summary>
/// BLAKE3-based content-addressing primitives. Single P/Invoke surface for the
/// native <c>BLAKE3HashService</c> kernel. Every entity / composition / edge
/// hash in the substrate flows through this interface — there is no managed-
/// side fallback, and no parallel implementation.
/// </summary>
public interface IIdentityHashing
{
    /// <summary>Hash a single atom's content bytes.</summary>
    AtomId AtomId(ReadOnlySpan<byte> content);

    /// <summary>
    /// Merkle hash of an ordered sequence of children with their RLE counts.
    /// Used to build composition entity hashes; identical child sequences
    /// produce identical parent hashes (deterministic, content-addressed).
    /// </summary>
    AtomId CompositionId(IReadOnlyList<AtomId> orderedChildren, IReadOnlyList<int> rleCounts);

    /// <summary>
    /// Hash of an edge: edge_type entity hash + role-ordered participant
    /// hashes. Edge type and roles are themselves substrate entities — never
    /// hardcoded English string codes.
    /// </summary>
    AtomId EdgeId(AtomId edgeType, IReadOnlyList<(AtomId Role, int RolePosition, AtomId Participant)> roleOrderedParticipants);

    /// <summary>
    /// Batch atom hashing — single P/Invoke amortizes managed-native crossing
    /// when seeding the codepoint pool or ingesting large token vocabularies.
    /// </summary>
    AtomId[] AtomIdBatch(IReadOnlyList<ReadOnlyMemory<byte>> contents);
}
