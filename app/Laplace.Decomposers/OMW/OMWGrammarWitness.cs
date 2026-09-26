using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public enum OmwType { Lemma, Def, Exe, Freq }
public readonly record struct OmwRow(
    long Offset, char SsType, string Lang, OmwType Type, bool Removed = false,
    double Magnitude = 1.0);

internal static class OMWEmitter
{
    // One spelling for the membership relation: the wn-data row confirms it and the
    // <lang>-changes.tab retraction refutes the same triple, in the same consensus cell.
    private const string MembershipRelation = "IS_SYNONYM_OF";

    internal static void Emit(
        SubstrateChangeBuilder b, in OmwRow row, ReadOnlySpan<byte> valueUtf8)
    {
        if (!TryAppendLemmaUtf8(b, valueUtf8, OMWDecomposer.Source, out var root))
            return;

        Hash128? synAnchor = ConceptAnchor.EmitAnchor(b, row.Offset, row.SsType, OMWDecomposer.Source);
        if (synAnchor is null) return;
        Hash128 synId = synAnchor.Value;

        Hash128 langId = LanguageReference.Emit(
            b, row.Lang, OMWDecomposer.Source, TC.AcademicCurated);
        OMWDecomposer.TrackLanguage(row.Lang);

        switch (row.Type)
        {
            case OmwType.Freq:
                // A wn-freq row ("this lemma was observed N times for this synset") testifies
                // to the same membership the wn-data row asserts and folds into that cell as
                // a scored witness: laplace_score_fp(n, 1.0) rises with n.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, MembershipRelation, synId, OMWDecomposer.Source, TC.AcademicCurated,
                    magnitude: row.Magnitude, arenaScale: 1.0, contextId: langId));
                break;
            case OmwType.Lemma when row.Removed:
                // A REMOVED row in <lang>-changes.tab says this lemma is no longer a member
                // of this synset. It refutes (confirm:false, score 0.0) the same triple the
                // wn-data lemma row confirms, in the same language context, so it contests
                // that consensus cell. MODIFIED rows are not acted on: they say the entry
                // changed, not that the membership was withdrawn.
                b.AddAttestation(NativeAttestation.Categorical(
                    root, MembershipRelation, synId, OMWDecomposer.Source, langId,
                    TC.AcademicCurated, confirm: false));
                break;
            case OmwType.Lemma:




                // Membership carries the file's language as context: one surface can be a
                // member of a synset in one language and not another (Danish "is" = ice),
                // so a reader scopes the attestation by the language that testified it.
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
