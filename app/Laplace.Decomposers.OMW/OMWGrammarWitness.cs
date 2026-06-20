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

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        if (!OMWRowParser.TryParseFields(
                composed.Composer.FieldSpans(), composed.Utf8, fileLang, out var row, out var valueUtf8))
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
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, EntityTypeRegistry.Language, OMWDecomposer.Source));

        switch (row.Type)
        {
            case OmwType.Lemma:
                // A lemma lexicalizes the shared ILI synset — the same fact WordNet emits as
                // lemma IS_SYNONYM_OF synset. IS_SYNONYM_OF (not IS_TRANSLATION_OF) converges OMW lemmas
                // with WordNet and lets cross-lingual translation EMERGE from two lemmas sharing one
                // synset, instead of asserting a word is a "translation of" a concept.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "IS_SYNONYM_OF", synId, OMWDecomposer.Source, null, TC.AcademicCurated));
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "HAS_LANGUAGE", langId, OMWDecomposer.Source, null, TC.AcademicCurated));
                // The synset's ss_type (a/n/v/r/s) is the lemma's POS in this sense. Emit HAS_POS on the
                // lemma with the WordNet tagset so OMW converges onto the same POS edges WordNet emits.
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
        return ContentWitnessBatch.TryAppendUnderscoredToBuilder(b, src, sourceId, out rootId);
    }

    private static void Trim(ref ReadOnlySpan<byte> src)
    {
        while (src.Length > 0 && src[0] == (byte)' ') src = src[1..];
        while (src.Length > 0 && src[^1] == (byte)' ') src = src[..^1];
    }
}
