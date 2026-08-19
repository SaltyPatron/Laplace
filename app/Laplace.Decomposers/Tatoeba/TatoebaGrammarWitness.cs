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
        b.AddEntity(new EntityRow(langId, EntityTier.Word, TatoebaDecomposer.LanguageTypeId, TatoebaDecomposer.Source));

        // The content root is the REAL sentence entity — content-addressed, UAX-tiered,
        // shared with any other source that ingests the same text (OpenSubtitles, a UAX
        // parse). See docs/specs/16 §2a.
        if (!ContentTierSpine.TryStageIntoBuilder(b, text, TatoebaDecomposer.Source, out var emitted))
            return;

        // What Tatoeba actually asserts about a sentence row: this text exists, and it is in
        // this language. Attested ONCE, at the root, which is the tier the source asserts it
        // at (docs/specs/16). The row NUMBER is not attested at all — see TatoebaIdMap.
        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));

        // The link phase's whole input. FREE here — the root is already composed — which is
        // the point of doing this in phase 1 instead of a prelude that resolves all 13.26M
        // roots a second time before the pipeline emits anything.
        _ids.Set(record.FirstId, emitted);

        _allowedIds?.TryAdd(record.FirstId, 0);
    }

    private void WalkLink(TatoebaIngestRecord record, SubstrateChangeBuilder b)
    {
        // links.csv is an ATTESTATION file, not an entity file. What Tatoeba asserts is
        // "this sentence is a translation of that sentence" — a fact between two CONTENT
        // ROOTS. The ids are scaffolding: they exist only because the links file cannot
        // inline the text, so they are resolved here and never stored.
        //
        // This lane used to mint a `tatoeba/sentence/{id}` entity per side and attest
        // between those. That is source-keyed identity — a row number promoted to an entity
        // id — which is exactly the entity-resolution table content addressing abolishes,
        // and it made every translation a read-side join across HAS_EXTERNAL_ID. MEASURED
        // at ~1.56 entity rows per link, the largest row category of the link phase.
        //
        // A link naming a sentence absent from sentences.csv is DROPPED, not grounded on a
        // synthetic node: an edge between two ids we cannot resolve to text asserts nothing
        // about language, and a bare anchor is an unattested node pretending otherwise.
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
