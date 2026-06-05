using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// THE canonical part-of-speech VALUE inventory — the omni-glottal POS
/// resolution index (the same at-ingest reference-resolution law as
/// <see cref="LanguageReference"/> for language codes and the Unicode script
/// entities for scripts).
///
/// <para>The kind layer was already unified (HAS_UPOS → HAS_POS alias;
/// HAS_XPOS its own finer arena) but the VALUE objects forked three ways —
/// <c>upos:NOUN</c> (UD) vs <c>wordnet/pos/n</c> vs <c>wiktionary/pos/noun</c>
/// — three consensus rows for "dog is a noun", ZERO co-assertion (2026-06-05
/// audit). This class owns the one canonical inventory and every source
/// tagset's resolution INTO it at ingest, so all POS witnesses land on the
/// literal same consensus pk.</para>
///
/// <para>The inventory is the 17 UPOS universal categories — language-generic
/// BY CONSTRUCTION (the Universal Dependencies cross-lingual tagset, not an
/// English-named concept vocabulary; the tag strings are canonical
/// identifiers rendered via <c>laplace.canonical_names</c>, never wordform
/// content). Granularity is NOT collapsed: language/tagset-specific tags
/// (Penn XPOS etc.) keep their own per-tagset values in the finer HAS_XPOS
/// arena; only the universal POS assertion resolves here.</para>
///
/// <para>Unknown source tags are NEVER silent and NEVER invented: they route
/// to a namespaced probationary value
/// (<c>substrate/pos/probationary/{tagset}/{tag}/v1</c>), increment
/// <see cref="ResolveMisses"/>, and land in <see cref="MissedTags"/> so the
/// mapping table grows from observed data, not guesswork.</para>
/// </summary>
public static class PosReference
{
    /// <summary>The POS value meta-type.</summary>
    public static readonly Hash128 PosTypeId = Hash128.OfCanonical("substrate/type/POS/v1");

    /// <summary>The 17 UPOS universal categories — THE canonical POS inventory.</summary>
    public static readonly string[] Canonical =
        ["ADJ", "ADP", "ADV", "AUX", "CCONJ", "DET", "INTJ", "NOUN", "NUM",
         "PART", "PRON", "PROPN", "PUNCT", "SCONJ", "SYM", "VERB", "X"];

    private static readonly HashSet<string> CanonSet = new(Canonical, StringComparer.Ordinal);

    /// <summary>Which source tagset a raw tag comes from (each has its own
    /// resolution table; probationary misses are namespaced per tagset).</summary>
    public enum PosTagset { Upos, WordNet, Wiktionary }

    // Wiktionary (wiktextract) pos strings → UPOS. Confident mappings only —
    // the long tail (phrase, proverb, prefix, romanization, …) is deliberately
    // probationary + logged, never guessed (2026-06-05 ruling).
    private static readonly Dictionary<string, string> WiktionaryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noun"] = "NOUN",   ["verb"] = "VERB",  ["adj"] = "ADJ",    ["adjective"] = "ADJ",
        ["adv"] = "ADV",     ["adverb"] = "ADV", ["pron"] = "PRON",  ["pronoun"] = "PRON",
        ["det"] = "DET",     ["determiner"] = "DET", ["article"] = "DET",
        ["adp"] = "ADP",     ["prep"] = "ADP",   ["preposition"] = "ADP", ["postp"] = "ADP", ["postposition"] = "ADP",
        ["num"] = "NUM",     ["numeral"] = "NUM",
        ["conj"] = "CCONJ",  ["conjunction"] = "CCONJ",
        ["intj"] = "INTJ",   ["interjection"] = "INTJ",
        ["particle"] = "PART",
        ["punct"] = "PUNCT", ["punctuation"] = "PUNCT", ["punctuation mark"] = "PUNCT",
        ["sym"] = "SYM",     ["symbol"] = "SYM",
        ["name"] = "PROPN",  ["proper noun"] = "PROPN",
        ["aux"] = "AUX",     ["auxiliary"] = "AUX",
    };

    private static long _resolveMisses;
    private static readonly ConcurrentDictionary<string, long> _missedTags = new(StringComparer.Ordinal);

    /// <summary>Unresolved-tag count (probationary routings) this process.</summary>
    public static long ResolveMisses => Interlocked.Read(ref _resolveMisses);

    /// <summary>The observed unmapped tags ("{tagset}:{tag}" → count) — the
    /// data-grown extension queue for the mapping tables.</summary>
    public static IReadOnlyDictionary<string, long> MissedTags => _missedTags;

    // Per-tag id memo: the canonical POS id is computed from one of only ~17 UPOS
    // strings, but Resolve() is on every token's hot path (UD UposId / Wiktionary
    // PosId). Without the memo each token re-formats + UTF8-encodes + BLAKE3s the
    // same string. Compute once per distinct tag, dictionary hit thereafter — the
    // established perf-cache discipline (content-addressed ⇒ a hit is bit-identical).
    private static readonly ConcurrentDictionary<string, Hash128> _canonicalIdMemo =
        new(StringComparer.Ordinal);

    /// <summary>Canonical value entity id for a UPOS tag.</summary>
    public static Hash128 CanonicalId(string upos) =>
        _canonicalIdMemo.GetOrAdd(upos, static u => Hash128.OfCanonical($"substrate/pos/{u}/v1"));

    /// <summary>UPOS tag → canonical tag (identity over the 17), else null.</summary>
    public static string? ResolveUpos(string tag) =>
        CanonSet.Contains(tag) ? tag : null;

    /// <summary>WordNet ss_type → canonical tag. Satellite adjectives (s) are
    /// adjectives — satellite-ness stays on the synset, not the POS value.</summary>
    public static string? ResolveWordNet(char ssType) => ssType switch
    {
        'n' => "NOUN", 'v' => "VERB", 'a' or 's' => "ADJ", 'r' => "ADV", _ => null,
    };

    /// <summary>wiktextract pos string → canonical tag, else null (probationary).</summary>
    public static string? ResolveWiktionary(string pos) =>
        WiktionaryMap.TryGetValue(pos, out var t) ? t : null;

    /// <summary>Resolve a source tag to the canonical POS value id — or the
    /// namespaced probationary value (+ miss counter + inventory), never silent,
    /// never thrown.</summary>
    public static Hash128 Resolve(string sourceTag, PosTagset tagset)
    {
        string? canon = tagset switch
        {
            PosTagset.Upos       => ResolveUpos(sourceTag),
            PosTagset.WordNet    => sourceTag.Length == 1 ? ResolveWordNet(sourceTag[0]) : null,
            PosTagset.Wiktionary => ResolveWiktionary(sourceTag),
            _ => null,
        };
        if (canon is not null) return CanonicalId(canon);

        Interlocked.Increment(ref _resolveMisses);
        string ns = tagset.ToString().ToLowerInvariant();
        _missedTags.AddOrUpdate($"{ns}:{sourceTag}", 1, static (_, c) => c + 1);
        return Hash128.OfCanonical($"substrate/pos/probationary/{ns}/{sourceTag}/v1");
    }

    /// <summary>Seed the canonical inventory: the POS meta-type entity + the 17
    /// canonical value entities. Called from each POS-emitting decomposer's
    /// InitializeAsync (idempotent — content-addressed ids, ON CONFLICT).</summary>
    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        builder.AddEntity(new EntityRow(PosTypeId, (byte)MetaTier.Meta,
            BootstrapIntentBuilder.TypeMetaTypeId, sourceId));
        foreach (var tag in Canonical)
            builder.AddEntity(new EntityRow(CanonicalId(tag), (byte)MetaTier.Meta, PosTypeId, sourceId));
    }
}
