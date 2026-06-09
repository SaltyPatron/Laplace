using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

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

        Hash128 extId = SourceEntityIdConventions.TatoebaSentence(sid);
        Hash128 langId = LanguageReference.Resolve(lang);
        b.AddEntity(new EntityRow(extId, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, TatoebaDecomposer.LanguageTypeId, TatoebaDecomposer.Source));

        if (composed.Composer.TrySpanEntity(fields[2].Start, fields[2].End, out var contentId))
        {
            b.AddAttestation(RelationTypeRegistry.Attest(
                contentId, "HAS_EXTERNAL_ID", extId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
            b.AddAttestation(RelationTypeRegistry.Attest(
                contentId, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
        }
        else
        {
            string text = FieldText(composed, fields[2]);
            if (ContentEmitter.Emit(b, text, TatoebaDecomposer.Source) is { } emitted)
            {
                b.AddAttestation(RelationTypeRegistry.Attest(
                    emitted, "HAS_EXTERNAL_ID", extId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
            }
        }
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

        Hash128 ea = SourceEntityIdConventions.TatoebaSentence(a);
        Hash128 eb = SourceEntityIdConventions.TatoebaSentence(bId);
        b.AddEntity(new EntityRow(ea, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddEntity(new EntityRow(eb, EntityTier.Vocabulary, TatoebaDecomposer.SentenceRefTypeId, TatoebaDecomposer.Source));
        b.AddAttestation(RelationTypeRegistry.Attest(
            ea, "IS_TRANSLATION_OF", eb, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
    }

    private static string FieldText(in GrammarComposeContext ctx, (uint Start, uint End) span) =>
        System.Text.Encoding.UTF8.GetString(ctx.Utf8.AsSpan((int)span.Start, (int)(span.End - span.Start)));
}
