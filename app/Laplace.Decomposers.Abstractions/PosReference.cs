using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class PosReference
{
    public static readonly Hash128 PosTypeId = Hash128.OfCanonical("substrate/type/POS/v1");

    public static readonly string[] Canonical =
        ["ADJ", "ADP", "ADV", "AUX", "CCONJ", "DET", "INTJ", "NOUN", "NUM",
         "PART", "PRON", "PROPN", "PUNCT", "SCONJ", "SYM", "VERB", "X"];

    private static readonly HashSet<string> CanonSet = new(Canonical, StringComparer.Ordinal);

    public enum PosTagset { Upos, WordNet, Wiktionary }

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

    public static long ResolveMisses => Interlocked.Read(ref _resolveMisses);

    public static IReadOnlyDictionary<string, long> MissedTags => _missedTags;

    private static readonly ConcurrentDictionary<string, Hash128> _canonicalIdMemo =
        new(StringComparer.Ordinal);

    public static Hash128 CanonicalId(string upos) =>
        _canonicalIdMemo.GetOrAdd(upos, static u => Hash128.OfCanonical($"substrate/pos/{u}/v1"));

    public static string? ResolveUpos(string tag) =>
        CanonSet.Contains(tag) ? tag : null;

    public static string? ResolveWordNet(char ssType) => ssType switch
    {
        'n' => "NOUN", 'v' => "VERB", 'a' or 's' => "ADJ", 'r' => "ADV", _ => null,
    };

    public static string? ResolveWiktionary(string pos) =>
        WiktionaryMap.TryGetValue(pos, out var t) ? t : null;

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

    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        builder.AddEntity(new EntityRow(PosTypeId, (byte)MetaTier.Meta,
            BootstrapIntentBuilder.TypeMetaTypeId, sourceId));
        foreach (var tag in Canonical)
            builder.AddEntity(new EntityRow(CanonicalId(tag), (byte)MetaTier.Meta, PosTypeId, sourceId));
    }
}
