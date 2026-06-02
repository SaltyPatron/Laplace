using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
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
///
/// <para><b>O(tier) short-circuit (the perf-cache principle, extended past T0).</b>
/// The codepoint perf-cache resolves a T0 atom → coord in O(1); but every composite tier
/// (grapheme / word / sentence) was re-derived on every call — <see cref="TextDecomposer"/>
/// + <see cref="HashComposer"/> + Build ran for EVERY occurrence of identical bytes, making
/// ingestion O(total occurrences) instead of O(distinct content per tier). A high-frequency
/// word paid the full cascade each time. The content memo below extends resolve-once-reuse
/// to composites: a distinct surface decomposes ONCE; every repeat is an O(1) dictionary hit.
/// Decomposition is deterministic and content-addressed, so a cache hit is bit-identical to
/// recomputing — a pure speedup, never a semantic change.</para>
/// </summary>
public static class ContentEmitter
{
    // Process-wide memos. Ingest runs one process per source (scripts/ingest-source.sh),
    // so a memo is naturally scoped to that source's DISTINCT content and freed at exit;
    // content-addressing keeps every entry valid for the life of the process. Bounded to
    // cap memory on the multi-hour corpora: once full, hits still serve — only new inserts
    // stop, and the high-frequency repeats are already resident.
    private const int MemoCap = 1 << 21; // ~2.1M distinct surfaces

    // (source, content-hash) → the rows actually added (T0 already filtered) + root id.
    private static readonly ConcurrentDictionary<(Hash128 Src, Hash128 Content),
        (Hash128 Root, ImmutableArray<EntityRow> Ents, ImmutableArray<PhysicalityRow> Phys)> _emitMemo = new();

    // content-hash → root id, source-independent (the content id is the same for every
    // source). Lets the attestation pass's RootId() hit the entity pass's decomposition.
    private static readonly ConcurrentDictionary<Hash128, Hash128?> _rootMemo = new();

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

        var contentHash = Hash128.Blake3(canonical);
        var key = (sourceId, contentHash);

        // O(1) hit: this exact surface already decomposed for this source — re-add the
        // (deterministic, content-addressed) rows; the writer dedups via ON CONFLICT.
        if (_emitMemo.TryGetValue(key, out var hit))
        {
            foreach (var e in hit.Ents) b.AddEntity(e);
            foreach (var p in hit.Phys) b.AddPhysicality(p);
            return hit.Root;
        }

        if (!TextEntityBuilder.TryBuildRows(canonical, sourceId,
                out var entities, out var physicalities, out var rootId, out _))
        {
            _rootMemo.TryAdd(contentHash, null);
            return null;
        }

        // Skip T0 codepoint entities + their physicalities: codepoints are universally seeded
        // by Unicode (always present), so re-emitting them per source is redundant DB traffic
        // and the main write-lock-contention / deadlock source under parallel ingestion. Higher
        // tiers (grapheme/word/sentence) are content-specific and ARE emitted; trajectories
        // still reference the seeded codepoint ids. Mirrors ContentRoundtrip.RecordAsync.
        var seededT0 = new HashSet<Hash128>();
        foreach (var e in entities) if (e.Tier == 0) seededT0.Add(e.Id);
        var ents = entities.Where(e => e.Tier != 0).ToImmutableArray();
        var phys = physicalities.Where(p => !seededT0.Contains(p.EntityId)).ToImmutableArray();

        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);

        if (_emitMemo.Count < MemoCap)
            _emitMemo.TryAdd(key, (rootId, ents, phys));
        _rootMemo.TryAdd(contentHash, rootId);
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
        var contentHash = Hash128.Blake3(bytes);

        // O(1) hit: the entity pass (or a prior RootId) already decomposed this surface.
        if (_rootMemo.TryGetValue(contentHash, out var cached)) return cached;

        Hash128? result = TextEntityBuilder.TryDecomposeRoot(bytes,
                out var rootId, out _, out _, out _, out _, out _)
            ? rootId : (Hash128?)null;
        _rootMemo.TryAdd(contentHash, result);
        return result;
    }
}
