using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Emits a surface string as substrate "normal content": routes the bytes through
/// <see cref="TextDecomposer"/> → <see cref="HashComposer"/> → <see cref="TextEntityBuilder"/>
/// so the text lands as a content-addressed Merkle entity + CONTENT physicality
/// (mantissa-packed trajectory per UAX-29 tier) — the SAME entity any other source
/// (the model tokenizer, a user prompt, another corpus) produces for the same bytes.
/// This is how a seed decomposer emits the "normal content" half of "seed = content +
/// attestations": attest onto the returned content-addressed id, never onto a
/// per-source string key.
///
/// <para>Observed bytes — NO lowercasing / NFC (ADR 0047): "The" and "the" are distinct
/// content. Callers pass the surface as-is (WordNet/OMW lemmas use '_' for spaces — the
/// caller replaces '_' with ' ' before calling so the phrase decomposes naturally).</para>
///
/// <para>Requires the codepoint perf-cache to be loaded by the host
/// (<see cref="CodepointPerfcache.Load"/>) before ingestion — same precondition every
/// text-bearing decomposer already has. Returns null when the text yields no tier tree
/// (empty / invalid UTF-8).</para>
/// </summary>
public static class ContentEmitter
{
    /// <summary>Emit content rows for <paramref name="surface"/> into
    /// <paramref name="b"/> and return its content-addressed root entity id, or
    /// null if the text yields no tier tree.</summary>
    public static Hash128? Emit(SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return Emit(b, Encoding.UTF8.GetBytes(surface), sourceId);
    }

    /// <summary>Byte overload of <see cref="Emit(SubstrateChangeBuilder,string,Hash128)"/>.</summary>
    public static Hash128? Emit(SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;
        if (!TextEntityBuilder.TryBuildRows(canonical, sourceId,
                out var entities, out var physicalities, out var rootId, out _))
            return null;
        // Skip T0 codepoint entities + their physicalities: codepoints are universally seeded
        // by Unicode (always present), so re-emitting them per source is redundant DB traffic
        // and the main write-lock-contention / deadlock source under parallel ingestion. Higher
        // tiers (grapheme/word/sentence) are content-specific and ARE emitted; trajectories
        // still reference the seeded codepoint ids. Mirrors ContentRoundtrip.RecordAsync.
        var seededT0 = new HashSet<Hash128>();
        foreach (var e in entities) if (e.Tier == 0) seededT0.Add(e.Id);
        foreach (var e in entities) if (e.Tier != 0) b.AddEntity(e);
        foreach (var p in physicalities) if (!seededT0.Contains(p.EntityId)) b.AddPhysicality(p);
        return rootId;
    }

    /// <summary>Compute the content-addressed root id for <paramref name="surface"/>
    /// WITHOUT emitting rows — for the attestation pass of a two-pass decomposer that
    /// already emitted the content rows in the entity pass. Same id as
    /// <see cref="Emit(SubstrateChangeBuilder,string,Hash128)"/> (decomposition is
    /// deterministic). Returns null if the text yields no tier tree.</summary>
    public static Hash128? RootId(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        var bytes = Encoding.UTF8.GetBytes(surface);
        if (!TextEntityBuilder.TryDecomposeRoot(bytes,
                out var rootId, out _, out _, out _, out _, out _))
            return null;
        return rootId;
    }
}
