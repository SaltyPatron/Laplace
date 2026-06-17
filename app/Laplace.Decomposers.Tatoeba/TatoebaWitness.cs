using System.Text;
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

        if (!ContentWitnessBatch.TryAppendToBuilder(
                b, row.TextUtf8, TatoebaDecomposer.Source, out var emitted))
            return;

        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_EXTERNAL_ID", extId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
        b.AddAttestation(NativeAttestation.Categorical(
            emitted, "HAS_LANGUAGE", langId, TatoebaDecomposer.Source, SourceTrust.StructuredCorpus));
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
