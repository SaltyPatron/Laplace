using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class PosReference
{
    public static readonly Hash128 PosTypeId = EntityTypeRegistry.Pos;

    public static readonly string[] Canonical =
        ["ADJ", "ADP", "ADV", "AUX", "CCONJ", "DET", "INTJ", "NOUN", "NUM",
         "PART", "PRON", "PROPN", "PUNCT", "SCONJ", "SYM", "VERB", "X"];

    public enum PosTagset { Upos, WordNet, Wiktionary }

    public static Hash128 CanonicalId(string upos) =>
        NativeAttestation.ResolvePos(upos, NativeAttestation.PosTagset.Upos);

    public static string? ResolveUpos(string tag) =>
        Canonical.Contains(tag) ? tag : null;

    public static string? ResolveWordNet(char ssType) => ssType switch
    {
        'n' => "NOUN", 'v' => "VERB", 'a' or 's' => "ADJ", 'r' => "ADV", _ => null,
    };

    public static string? ResolveWiktionary(string pos)
    {
        var id = NativeAttestation.ResolvePos(pos, NativeAttestation.PosTagset.Wiktionary);
        foreach (var c in Canonical)
            if (CanonicalId(c).Equals(id)) return c;
        return null;
    }

    public static Hash128 Resolve(string sourceTag, PosTagset tagset) =>
        NativeAttestation.ResolvePos(sourceTag, (NativeAttestation.PosTagset)tagset);

    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        builder.AddEntity(new EntityRow(PosTypeId, EntityTier.Vocabulary,
            BootstrapIntentBuilder.TypeMetaTypeId, sourceId));
        foreach (var tag in Canonical)
            builder.AddEntity(new EntityRow(CanonicalId(tag), EntityTier.Vocabulary, PosTypeId, sourceId));
    }
}
