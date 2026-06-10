using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

internal static class TatoebaWitness
{
    public static void WalkSentence(in TatoebaSentenceRow row, SubstrateChangeBuilder b)
    {
        Hash128 extId = SourceEntityIdConventions.TatoebaSentence(row.Id);
        Hash128 langId = LanguageReference.Resolve(row.Lang);
        b.AddEntity(new EntityRow(extId, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, TatoebaDecomposer.LanguageTypeId, TatoebaDecomposer.Source));

        if (ContentEmitter.Emit(b, row.Text, TatoebaDecomposer.Source) is { } emitted)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                emitted, "HAS_EXTERNAL_ID", extId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
        }
    }

    public static void WalkLink(in TatoebaLinkRow row, SubstrateChangeBuilder b)
    {
        Hash128 ea = SourceEntityIdConventions.TatoebaSentence(row.A);
        Hash128 eb = SourceEntityIdConventions.TatoebaSentence(row.B);
        b.AddEntity(new EntityRow(ea, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(eb, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddAttestation(NativeAttestation.Categorical(
            ea, "IS_TRANSLATION_OF", eb, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
    }
}

internal sealed class TatoebaSentenceWitness : IGrammarWitness
{
    private readonly HashSet<long>? _allowedIds;
    private readonly LanguageFilter? _langs;

    public TatoebaSentenceWitness(HashSet<long>? allowedIds, LanguageFilter? langs = null)
    {
        _allowedIds = allowedIds;
        _langs = langs;
    }

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        if (fields.Count < 3) return;
        if (fields[2].End <= fields[2].Start) return;

        string idText = FieldText(composed, fields[0]);
        if (!long.TryParse(idText, out long sid)) return;

        string lang = FieldText(composed, fields[1]).Trim();
        if (_langs?.MatchesRaw(lang) == false) return;
        _allowedIds?.Add(sid);

        TatoebaWitness.WalkSentence(new TatoebaSentenceRow
        {
            Id = sid,
            Lang = lang,
            Text = FieldText(composed, fields[2]),
        }, b);
    }

    private static string FieldText(in GrammarComposeContext ctx, (uint Start, uint End) span) =>
        System.Text.Encoding.UTF8.GetString(ctx.Utf8.AsSpan((int)span.Start, (int)(span.End - span.Start)));
}

internal sealed class TatoebaLinkWitness : IGrammarWitness
{
    private readonly HashSet<long>? _allowedIds;

    public TatoebaLinkWitness(HashSet<long>? allowedIds) => _allowedIds = allowedIds;

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        if (fields.Count < 2) return;

        if (!long.TryParse(FieldText(composed, fields[0]), out long a)) return;
        if (!long.TryParse(FieldText(composed, fields[1]), out long bId)) return;
        if (_allowedIds is not null && (!_allowedIds.Contains(a) || !_allowedIds.Contains(bId)))
            return;

        TatoebaWitness.WalkLink(new TatoebaLinkRow { A = a, B = bId }, b);
    }

    private static string FieldText(in GrammarComposeContext ctx, (uint Start, uint End) span) =>
        System.Text.Encoding.UTF8.GetString(ctx.Utf8.AsSpan((int)span.Start, (int)(span.End - span.Start)));
}
