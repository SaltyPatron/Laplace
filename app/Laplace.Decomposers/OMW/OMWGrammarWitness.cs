using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public enum OmwType { Lemma, Def, Exe }
public readonly record struct OmwRow(long Offset, char SsType, string Lang, OmwType Type);

internal sealed class OMWGrammarWitness(string fileLang) : IGrammarWitness
{
    public string ModalityId => "tsv";

    public bool TrunkShortcircuitWithoutCompose => true;

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        OmwRow row;
        ReadOnlySpan<byte> valueUtf8;
        if (composed.Composer is { } composer)
        {
            if (!OMWRowParser.TryParseFields(
                    composer.FieldSpans(), composed.Utf8, fileLang, out row, out valueUtf8))
                return;
        }
        else if (!OMWRowParser.TryParseRow(composed.Utf8, fileLang, out row, out valueUtf8))
            return;

        EmitRow(b, row, valueUtf8);
    }

    private static void EmitRow(SubstrateChangeBuilder b, in OmwRow row, ReadOnlySpan<byte> valueUtf8)
    {
        if (!TryAppendLemmaUtf8(b, valueUtf8, OMWDecomposer.Source, out var root))
            return;

        Hash128? synAnchor = ConceptAnchor.EmitAnchor(b, row.Offset, row.SsType, OMWDecomposer.Source);
        if (synAnchor is null) return;
        Hash128 synId = synAnchor.Value;
        ConceptAnchor.AttestSynsetCategory(b, synId, OMWDecomposer.Source, TC.AcademicCurated);

        Hash128 langId = LanguageReference.Resolve(row.Lang);
        OMWDecomposer.TrackLanguage(row.Lang);
        b.AddEntity(new EntityRow(langId, EntityTier.Word, EntityTypeRegistry.Language, OMWDecomposer.Source));

        switch (row.Type)
        {
            case OmwType.Lemma:




                b.AddAttestation(NativeAttestation.Categorical(
                    root, "IS_SYNONYM_OF", synId, OMWDecomposer.Source, null, TC.AcademicCurated));
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "HAS_LANGUAGE", langId, OMWDecomposer.Source, null, TC.AcademicCurated));


                PosReference.Attest(b, root, row.SsType.ToString(), PosReference.PosTagset.WordNet,
                    OMWDecomposer.Source, null, TC.AcademicCurated);
                break;
            case OmwType.Def:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_DEFINITION", root, OMWDecomposer.Source, langId, TC.AcademicCurated));
                break;
            case OmwType.Exe:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_EXAMPLE", root, OMWDecomposer.Source, langId, TC.AcademicCurated));
                break;
        }
    }

    private static bool TryAppendLemmaUtf8(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> src, Hash128 sourceId, out Hash128 rootId)
    {
        Trim(ref src);
        if (src.IsEmpty) { rootId = default; return false; }
        return ContentTierSpine.TryStageUnderscoredIntoBuilder(b, src, sourceId, out rootId);
    }

    private static void Trim(ref ReadOnlySpan<byte> src)
    {
        while (src.Length > 0 && src[0] == (byte)' ') src = src[1..];
        while (src.Length > 0 && src[^1] == (byte)' ') src = src[..^1];
    }
}
