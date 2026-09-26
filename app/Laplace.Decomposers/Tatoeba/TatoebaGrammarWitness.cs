using System.Text;
using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

internal enum TatoebaRowKind { Sentence, Link }
internal readonly record struct TatoebaIngestRecord(
    long FirstId, long SecondId, string? Language, byte[]? TextUtf8);

internal sealed class TatoebaEmitter
{
    private readonly TatoebaRowKind _kind;
    private readonly ConcurrentDictionary<long, byte>? _allowedIds;
    private readonly TatoebaIdMap _ids;

    public TatoebaEmitter(
        TatoebaRowKind kind, ConcurrentDictionary<long, byte>? allowedIds, TatoebaIdMap ids)
    {
        _kind = kind;
        _allowedIds = allowedIds;
        _ids = ids;
    }

    public void Emit(TatoebaIngestRecord record, SubstrateChangeBuilder b)
    {
        switch (_kind)
        {
            case TatoebaRowKind.Sentence:
                WalkSentence(record, b);
                break;
            case TatoebaRowKind.Link:
                WalkLink(record, b);
                break;
        }
    }

    private void WalkSentence(TatoebaIngestRecord record, SubstrateChangeBuilder b)
    {
        if (record.Language is not { Length: > 0 } lang || record.TextUtf8 is not { Length: > 0 } text)
            return;

        // Resolve the language code once; id + readback tracking both reuse it.
        string? iso3 = LanguageReference.ResolveCode(lang);
        Hash128 langId = LanguageReference.IdForResolvedCode(iso3);
        VocabularyNames.TrackResolvedLanguage(TatoebaDecomposer.LanguageNames, iso3);
        b.AddEntity(new EntityRow(langId, EntityTier.Word, TatoebaDecomposer.LanguageTypeId));

        // The content root is the sentence entity: content-addressed, UAX-tiered, and the
        // same entity any other source reaches for the same text.
        if (!ContentTierSpine.TryStageIntoBuilder(b, text, TatoebaDecomposer.Source, out var emitted))
            return;

        // A sentence row testifies that this text is in this language, attested once on
        // the root. The row number is not attested.
        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));

        // Record row id → root for link resolution; the root is already composed here.
        _ids.Set(record.FirstId, emitted);

        _allowedIds?.TryAdd(record.FirstId, 0);
    }

    private void WalkLink(TatoebaIngestRecord record, SubstrateChangeBuilder b)
    {
        // A links.csv row testifies that one sentence translates another: an attestation
        // between two content roots. The row ids are packaging, resolved here and never
        // stored. A link naming an id absent from sentences.csv is dropped, not attached to
        // a synthetic entity: it names no text, so it asserts nothing.
        if (!_ids.TryGet(record.FirstId, out var rootA)
            || !_ids.TryGet(record.SecondId, out var rootB))
        {
            Interlocked.Increment(ref TatoebaDecomposer.UnresolvedLinks);
            return;
        }
        if (rootA.Equals(rootB)) return;  // identical text on both sides is not a translation

        b.AddAttestation(NativeAttestation.Categorical(
            rootA, "IS_TRANSLATION_OF", rootB, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
    }

}

internal static class TatoebaParse
{
    public static bool TrySentence(ReadOnlySpan<byte> line, out TatoebaIngestRecord record)
    {
        record = default;
        int firstTab = line.IndexOf((byte)'\t');
        if (firstTab <= 0) return false;
        ReadOnlySpan<byte> tail = line[(firstTab + 1)..];
        int secondTab = tail.IndexOf((byte)'\t');
        if (secondTab <= 0) return false;
        if (!TryInt64(line[..firstTab], out long id)) return false;

        string language = Encoding.UTF8.GetString(tail[..secondTab]).Trim();
        ReadOnlySpan<byte> text = tail[(secondTab + 1)..];
        if (language.Length == 0 || text.IsEmpty) return false;
        record = new TatoebaIngestRecord(id, 0, language, text.ToArray());
        return true;
    }

    public static bool TryLink(ReadOnlySpan<byte> line, out TatoebaIngestRecord record)
    {
        record = default;
        int firstTab = line.IndexOf((byte)'\t');
        if (firstTab <= 0) return false;
        ReadOnlySpan<byte> tail = line[(firstTab + 1)..];
        int secondTab = tail.IndexOf((byte)'\t');
        ReadOnlySpan<byte> second = secondTab < 0 ? tail : tail[..secondTab];
        if (!TryInt64(line[..firstTab], out long first)
            || !TryInt64(second, out long other))
            return false;
        record = new TatoebaIngestRecord(first, other, null, null);
        return true;
    }

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
