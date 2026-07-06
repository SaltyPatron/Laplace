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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Hash128> _idToRoot;

    public TatoebaGrammarWitness(
        TatoebaRowKind kind,
        HashSet<long>? allowedIds,
        System.Collections.Concurrent.ConcurrentDictionary<long, Hash128> idToRoot)
    {
        _kind = kind;
        _allowedIds = allowedIds;
        _idToRoot = idToRoot;
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
        Hash128 langId = LanguageReference.Resolve(lang);
        VocabularyNames.TrackLanguage(TatoebaDecomposer.LanguageNames, lang);
        b.AddEntity(new EntityRow(extId, EntityTier.Word, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(langId, EntityTier.Word, TatoebaDecomposer.LanguageTypeId, TatoebaDecomposer.Source));

        // The content root is the REAL sentence entity — content-addressed, UAX-tiered,
        // shared with any other source that ingests the same text (OpenSubtitles, a UAX
        // parse). The Tatoeba numeric id becomes a mere external-id annotation ON that
        // root (HAS_EXTERNAL_ID → extId), never the sentence's identity. See .scratchpad/16 §2a.
        if (!ContentWitnessBatch.TryAppendToBuilder(b, text, TatoebaDecomposer.Source, out var emitted))
            return;

        // Record id → content root so the link epoch can anchor IS_TRANSLATION_OF on the
        // real content roots (links.csv carries only numeric ids, no text).
        _idToRoot[id] = emitted;

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

        // Anchor translation on the CONTENT ROOTS resolved from the sentence epoch, not on
        // synthetic per-id ref entities. If either sentence was absent/filtered/empty (no
        // root recorded), the link cannot be grounded in content — skip it rather than mint
        // a translation between two id-hashes. See .scratchpad/16 §2a.
        if (!_idToRoot.TryGetValue(a, out var rootA)) return;
        if (!_idToRoot.TryGetValue(bId, out var rootB)) return;

        b.AddAttestation(NativeAttestation.Categorical(
            rootA, "IS_TRANSLATION_OF", rootB, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
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
