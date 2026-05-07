namespace Laplace.Decomposers.Text;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F1 — the canonical text decomposer. Takes a string, decomposes it into
/// the substrate's content-addressed model:
///
///   - Each Unicode codepoint resolves to its tier-0 entity_hash via
///     <see cref="ICodepointPool"/> (microsecond lookup, no DB).
///   - The codepoint LINESTRING is RLE-encoded (adjacent identical
///     codepoints collapse into one entity_child row with rle_count > 1).
///   - The composition entity_hash is computed as the BLAKE3 Merkle of
///     (child_hash, rle_count) pairs via <see cref="IIdentityHashing"/>.
///   - One <see cref="EntityRecord"/> emitted for the new tier-1
///     composition; one <see cref="EntityChildRecord"/> emitted per RLE
///     run.
///
/// Cross-source dedup is automatic: same content always produces the same
/// composition hash, so emitting "cat" from WordNet + Wiktionary +
/// Tatoeba lands on the SAME entity_hash with multiple provenance edges.
/// Per substrate invariants 1, 3, 4: content = identity, dedup is maximal,
/// knowledge accumulates as the edge graph density.
///
/// Phase 4 / Track F / F1 — the foundational text decomposer that every
/// text-bearing seed decomposer (WordNet, OMW, UD, Wiktionary, Tatoeba,
/// ATOMIC, ArXiv, AI model tokenizers) cascades through.
/// </summary>
public sealed class TextDecomposer
{
    private readonly ICodepointPool      _codepoints;
    private readonly IIdentityHashing    _hashing;
    private readonly IEntityEmission     _entityEmission;
    private readonly IEntityChildEmission _childEmission;

    public TextDecomposer(
        ICodepointPool       codepoints,
        IIdentityHashing     hashing,
        IEntityEmission      entityEmission,
        IEntityChildEmission childEmission)
    {
        _codepoints     = codepoints;
        _hashing        = hashing;
        _entityEmission = entityEmission;
        _childEmission  = childEmission;
    }

    /// <summary>
    /// Decompose <paramref name="text"/> into the substrate's content-
    /// addressed entity model. Returns the composition entity hash for
    /// the input string. Idempotent: calling with the same text always
    /// yields the same hash, and re-emission against the database is a
    /// no-op (ON CONFLICT DO NOTHING in the emission path).
    /// </summary>
    public async Task<AtomId> DecomposeAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            // The empty string is a valid substrate entity — its content is
            // empty bytes. BLAKE3 of empty input is well-defined.
            var empty = _hashing.AtomId(ReadOnlySpan<byte>.Empty);
            await _entityEmission.EmitAsync(
                new EntityRecord(empty, Tier: 1, ContentKindHash: empty,
                                 Content: Array.Empty<byte>(),
                                 Centroid: new Point4D(0, 0, 0, 1)),
                cancellationToken).ConfigureAwait(false);
            return empty;
        }

        // 1) Resolve each codepoint to its substrate entity_hash.
        var codepointHashes = new List<AtomId>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            codepointHashes.Add(_codepoints.AtomIdFor(rune.Value));
        }

        // 2) RLE-encode adjacent identical hashes (per substrate invariant 3).
        var runs = new List<(AtomId Hash, int Count)>();
        var current = codepointHashes[0];
        var count = 1;
        for (int i = 1; i < codepointHashes.Count; ++i)
        {
            if (HashEquals(codepointHashes[i], current))
            {
                ++count;
            }
            else
            {
                runs.Add((current, count));
                current = codepointHashes[i];
                count = 1;
            }
        }
        runs.Add((current, count));

        // 3) Compute the tier-1 composition hash (Merkle over runs).
        var children = new List<AtomId>(runs.Count);
        var counts   = new List<int>(runs.Count);
        foreach (var (h, c) in runs)
        {
            children.Add(h);
            counts.Add(c);
        }
        var compositionHash = _hashing.CompositionId(children, counts);

        // 4) Emit the composition entity. Centroid = vertex centroid of
        //    constituents — but in this decomposer we don't have the codepoint
        //    positions in scope, so emit (0,0,0,0) and let the
        //    PhysicalityEmission service populate the position once it can
        //    look up codepoint positions from the C codepoint table.
        await _entityEmission.EmitAsync(
            new EntityRecord(
                compositionHash,
                Tier: 1,
                ContentKindHash: compositionHash,
                Content: null,
                Centroid: new Point4D(0, 0, 0, 0)),
            cancellationToken).ConfigureAwait(false);

        // 5) Emit one entity_child row per RLE run.
        for (int ordinal = 0; ordinal < runs.Count; ++ordinal)
        {
            var (childHash, rleCount) = runs[ordinal];
            await _childEmission.EmitAsync(
                new EntityChildRecord(
                    ParentHash: compositionHash,
                    Ordinal:    ordinal,
                    RleCount:   rleCount,
                    ChildHash:  childHash),
                cancellationToken).ConfigureAwait(false);
        }

        return compositionHash;
    }

    private static bool HashEquals(AtomId a, AtomId b)
    {
        var sa = a.AsSpan();
        var sb = b.AsSpan();
        if (sa.Length != sb.Length) { return false; }
        for (int i = 0; i < sa.Length; ++i)
        {
            if (sa[i] != sb[i]) { return false; }
        }
        return true;
    }
}
