using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical path conventions for vocabulary entities (POS, UD morphology, relation types, …)
/// that must be registered into <c>canonical_names</c> so <c>render()</c>/<c>label()</c> resolve
/// human-readable text instead of TYPE:hash fallbacks.
/// </summary>
public static class VocabularyNames
{
    public static string UdFeatureValue(string name, string value) =>
        $"ud/feature/{name}={value}";

    public static string RelationType(string canonical) =>
        $"substrate/type/{canonical}/v1";

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

    public static Hash128 UdFeatureValueId(string name, string value) =>
        Hash128.OfCanonical(UdFeatureValue(name, value));
}
