using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

internal enum TatoebaRowKind { Sentence, Link }

internal sealed class TatoebaGrammarWitness : IGrammarWitness
{
    private readonly TatoebaRowKind _kind;
    private readonly HashSet<long>? _allowedIds;

    public TatoebaGrammarWitness(TatoebaRowKind kind, HashSet<long>? allowedIds)
    {
        _kind = kind;
        _allowedIds = allowedIds;
    }

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        ReadOnlySpan<byte> utf8 = composed.Utf8;
        switch (_kind)
        {
            case TatoebaRowKind.Sentence:
                WalkSentence(fields, utf8, b);
                break;
            case TatoebaRowKind.Link:
                WalkLink(fields, utf8, b);
                break;
        }
    }

    private void WalkSentence(
        IReadOnlyList<(uint Start, uint End)> fields, ReadOnlySpan<byte> utf8, SubstrateChangeBuilder b)
    {
        if (fields.Count < 3) return;
        if (!TatoebaParse.TryInt64(Slice(utf8, fields[0]), out long id)) return;
        string lang = Encoding.UTF8.GetString(Slice(utf8, fields[1])).Trim();
        ReadOnlySpan<byte> text = Slice(utf8, fields[2]);
        if (text.IsEmpty) return;

        Hash128 extId = SourceEntityIdConventions.TatoebaSentence(id);
        // Resolve the language code once; id + readback tracking both reuse it.
        string? iso3 = LanguageReference.ResolveCode(lang);
        Hash128 langId = LanguageReference.IdForResolvedCode(iso3);
        VocabularyNames.TrackResolvedLanguage(TatoebaDecomposer.LanguageNames, iso3);
        b.AddEntity(new EntityRow(extId, EntityTier.Word, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(langId, EntityTier.Word, TatoebaDecomposer.LanguageTypeId, TatoebaDecomposer.Source));

        // The content root is the REAL sentence entity — content-addressed, UAX-tiered,
        // shared with any other source that ingests the same text (OpenSubtitles, a UAX
        // parse). The Tatoeba numeric id becomes a mere external-id annotation ON that
        // root (HAS_EXTERNAL_ID → extId), never the sentence's identity. See .scratchpad/16 §2a.
        if (!ContentTierSpine.TryStageIntoBuilder(b, text, TatoebaDecomposer.Source, out var emitted))
            return;

        // The HAS_EXTERNAL_ID bridge (content root -> deterministic TatoebaSentence(id) anchor) is
        // what the link lane's IS_TRANSLATION_OF resolves against at read time — no runtime map.
        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_EXTERNAL_ID", extId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));

        _allowedIds?.Add(id);
    }

    private void WalkLink(
        IReadOnlyList<(uint Start, uint End)> fields, ReadOnlySpan<byte> utf8, SubstrateChangeBuilder b)
    {
        if (fields.Count < 2) return;
        if (!TatoebaParse.TryInt64(Slice(utf8, fields[0]), out long a)) return;
        if (!TatoebaParse.TryInt64(Slice(utf8, fields[1]), out long bId)) return;

        // Content-addressed and ORDER-INDEPENDENT: the link is witnessed verbatim between the two
        // DETERMINISTIC external-id anchors TatoebaSentence(id) — computed here from the ids alone,
        // no runtime id->root map, so links need not run after sentences. Each anchor bridges to its
        // real content root via HAS_EXTERNAL_ID (attested in WalkSentence), so the content-root-level
        // translation (and cross-source merge) is the DERIVED read-side join across that bridge —
        // record vs calculate. The anchor is also load-bearing on its own: it links ILI/concept
        // hashes (through the sentence's tier entities) back to Tatoeba records for readback and
        // direct-translation accuracy checks.
        Hash128 refA = SourceEntityIdConventions.TatoebaSentence(a);
        Hash128 refB = SourceEntityIdConventions.TatoebaSentence(bId);
        // Mint the ref anchors here so the edge is referentially sound even if a sentence is absent
        // (like WordNet referencing a synset defined in another file). If the sentence IS present,
        // WalkSentence emits the same id (a content-addressed collision) plus the HAS_EXTERNAL_ID
        // bridge to its content root; if absent, the anchor stays bare (ungrounded, no bridge).
        b.AddEntity(new EntityRow(refA, EntityTier.Word, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(refB, EntityTier.Word, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddAttestation(NativeAttestation.Categorical(
            refA, "IS_TRANSLATION_OF", refB, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, (uint Start, uint End) sp) =>
        utf8[(int)sp.Start..(int)sp.End];
}

internal static class TatoebaParse
{
    public static bool TryInt64(ReadOnlySpan<byte> s, out long v)
    {
        v = 0;
        if (s.IsEmpty) return false;
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') return false;
            v = checked(v * 10 + (c - (byte)'0'));
        }
        return true;
    }
}
