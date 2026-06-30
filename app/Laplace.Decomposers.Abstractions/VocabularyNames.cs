using System.Collections.Concurrent;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical path conventions for vocabulary entities (POS, UD morphology, relation types, …)
/// registered into <c>canonical_names</c> so <c>render()</c>/<c>label()</c> resolve human-readable
/// text instead of TYPE:hash fallbacks. Keys must match the content input to blake3 that produced the
/// entity's id — i.e. <c>HighwayPerfcache.NodeHash(key)</c> round-trips to the same entity hash.
/// </summary>
public static class VocabularyNames
{
    // Content key: the entity id is blake3(utf8("{name}={value}")), so the canonical_names key is the
    // same string — no path prefix.
    public static string UdFeatureValue(string name, string value) => $"{name}={value}";

    // Content key: relation type entity id is blake3(utf8(canonical)), matching EntityTypeRegistry.Id.
    public static string RelationType(string canonical) => canonical;

    public static string LanguageIso639_3(string iso3) =>
        $"language:{iso3.ToLowerInvariant()}";

    public static string ProbationaryPos(PosReference.PosTagset tagset, string tag)
    {
        string ns = tagset switch
        {
            PosReference.PosTagset.Upos       => "upos",
            PosReference.PosTagset.WordNet   => "wordnet",
            PosReference.PosTagset.Wiktionary => "wiktionary",
            PosReference.PosTagset.FrameNet  => "framenet",
            _ => "unknown",
        };
        return $"substrate/pos/probationary/{ns}/{tag}/v1";
    }

    public static void Track(ConcurrentDictionary<string, byte>? names, string canonical) =>
        names?.TryAdd(canonical, 0);

    public static void TrackUdFeatureValue(
        ConcurrentDictionary<string, byte>? names, string name, string value) =>
        Track(names, UdFeatureValue(name, value));

    public static void TrackLanguage(
        ConcurrentDictionary<string, byte>? names, string? langInput)
    {
        if (names is null || string.IsNullOrWhiteSpace(langInput)) return;
        string? iso3 = LanguageReference.ResolveCode(langInput);
        if (iso3 is not null)
            Track(names, LanguageIso639_3(iso3));
    }

    public static void TrackProbationaryPos(
        ConcurrentDictionary<string, byte>? names, string tag, PosReference.PosTagset tagset)
    {
        if (names is null) return;
        NativeAttestation.ResolvePos(tag, tagset, out bool probationary);
        if (probationary)
            Track(names, ProbationaryPos(tagset, tag));
    }

}
