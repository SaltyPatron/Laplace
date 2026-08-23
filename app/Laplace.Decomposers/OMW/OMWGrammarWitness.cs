using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public enum OmwType { Lemma, Def, Exe }
public readonly record struct OmwRow(
    long Offset, char SsType, string Lang, OmwType Type, bool Removed = false);

internal static class OMWEmitter
{
    // One spelling for the membership relation: the wn-data row asserts it and the
    // <lang>-changes.tab retraction refutes the SAME triple, so the two must never be
    // able to drift onto different relations.
    private const string MembershipRelation = "IS_SYNONYM_OF";

    internal static void Emit(
        SubstrateChangeBuilder b, in OmwRow row, ReadOnlySpan<byte> valueUtf8)
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
            case OmwType.Lemma when row.Removed:
                // OMW ships its own retractions in <lang>-changes.tab and they were never
                // globbed: 3,279 REMOVED rows across 26 files, each saying this lemma is no
                // longer a member of this synset. Dropping them meant the substrate could
                // only ever accumulate membership -- a corpus that took a word back had no
                // way to say so.
                //
                // This refutes the SAME triple the wn-data lemma row asserts, in the same
                // language context, so the retraction meets the assertion in one consensus
                // cell and contests it instead of landing somewhere it can never be seen.
                // outcome is the field for that; confirm:false is a Refute (score 0.0).
                //
                // MODIFIED rows (129) are deliberately NOT touched: the action says the
                // entry changed, not that the membership is withdrawn, and guessing which
                // half changed would be inventing testimony the source did not give.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, MembershipRelation, synId, OMWDecomposer.Source, langId,
                    TC.AcademicCurated, confirm: false));
                break;
            case OmwType.Lemma:




                // contextId = langId. OMW reads one file per language, so the
                // language of every lemma->synset membership is known here and was
                // being discarded. HAS_DEFINITION and HAS_EXAMPLE below already pass
                // langId; these two did not, and the cross-lingual edge is the one
                // that most needs the scope.
                //
                // MEASURED 2026-08-04, before this fix: the surface "is" gained
                // IS_SYNONYM_OF -> "ice" with 9 witnesses (Danish/Norwegian/Dutch for
                // ice) against 1 witness for English "is". With a NULL context no
                // reader could tell which language attested the edge — the language
                // lives on the surface but not on the sense and not on the edge — so
                // the English copula elected "ice" and election_correctness fell from
                // 5/6 to 2/6, with four of six probes answering "ice". GH #867.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, MembershipRelation, synId, OMWDecomposer.Source, langId, TC.AcademicCurated));
                // HAS_LANGUAGE keeps a null context: the object IS the language, so a
                // language context would be circular.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "HAS_LANGUAGE", langId, OMWDecomposer.Source, null, TC.AcademicCurated));

                // Part of speech is per-language too — the tagset is WordNet's, but
                // the claim "this surface is a noun" holds in the language the file
                // was written for, not universally.
                PosReference.Attest(b, root, row.SsType.ToString(), PosReference.PosTagset.WordNet,
                    OMWDecomposer.Source, langId, TC.AcademicCurated);
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
