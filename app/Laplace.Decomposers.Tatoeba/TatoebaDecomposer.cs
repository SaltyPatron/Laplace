using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

namespace Laplace.Decomposers.Tatoeba;

/// <summary>
/// Emits the Tatoeba multilingual sentence corpus as content + attestations.
///
/// Each sentence (sentences.csv: id ⇥ lang ⇥ text) is decomposed as content
/// (ContentEmitter) — so 13.26M sentences become real, deduped, queryable content that
/// converges with every other source. The Tatoeba numeric id is a join key, NOT identity:
/// it's carried as a HAS_EXTERNAL_ID attestation on the content. Translation pairs
/// (links.csv: id ⇥ id) become IS_TRANSLATION_OF between the external-id entities — bounded
/// memory (no 13M-entry id→hash map): the content↔content translation is recoverable by
/// joining through HAS_EXTERNAL_ID.
///
/// Two passes: pass 1 sentences (content + external-id entity + HAS_EXTERNAL_ID +
/// HAS_LANGUAGE); pass 2 links (IS_TRANSLATION_OF). Audio metadata is deferred to the
/// audio modality.
/// </summary>
public sealed class TatoebaDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TatoebaDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 SentenceRefTypeId =
        Hash128.OfCanonical("substrate/type/Tatoeba_Sentence/v1");
    private static readonly Hash128 LanguageTypeId =
        Hash128.OfCanonical("substrate/type/Language/v1");

    private static Hash128 Kind(string n) => Hash128.OfCanonical($"substrate/kind/{n}/v1");
    private static readonly Hash128 KindHasExternalId   = Kind("HAS_EXTERNAL_ID");
    private static readonly Hash128 KindHasLanguage     = Kind("HAS_LANGUAGE");
    private static readonly Hash128 KindIsTranslationOf = Kind("IS_TRANSLATION_OF");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "TatoebaDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Tatoeba_Sentence");
        boot.AddKind("HAS_EXTERNAL_ID",  KindValueTier.T2, TC.StructuredCorpusTier5);
        boot.AddKind("IS_TRANSLATION_OF", KindValueTier.T6, TC.StructuredCorpusTier5);
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string sentences = Path.Combine(context.EcosystemPath, "sentences.csv");
        string links     = Path.Combine(context.EcosystemPath, "links.csv");
        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;

        // Pass 1: sentences → content + external-id + HAS_LANGUAGE.
        if (File.Exists(sentences))
        {
            var b = NewBuilder("tatoeba/sent-0", batch);
            int n = 0, bn = 0;
            await foreach (var line in File.ReadLinesAsync(sentences, ct))
            {
                ct.ThrowIfCancellationRequested();
                var c = line.Split('\t');
                if (c.Length < 3) continue;
                if (!long.TryParse(c[0], out long sid)) continue;
                string lang = c[1].Trim();
                string text = c[2];
                if (text.Length == 0) continue;

                Hash128 extId = SourceEntityIdConventions.TatoebaSentence(sid);
                Hash128 langId = LanguageReference.Resolve(lang);
                b.AddEntity(new EntityRow(extId, /*tier*/ 3, SentenceRefTypeId, Source));
                b.AddEntity(new EntityRow(langId, /*tier*/ 2, LanguageTypeId, Source));

                var contentId = ContentEmitter.Emit(b, text, Source);
                if (contentId is not null)
                {
                    b.AddAttestation(AttestationFactory.Create(
                        contentId.Value, KindHasExternalId, extId, Source, null,
                        KindValueTier.T2, TC.StructuredCorpusTier5));
                    b.AddAttestation(AttestationFactory.Create(
                        contentId.Value, KindHasLanguage, langId, Source, null,
                        KindValueTier.T4, TC.StructuredCorpusTier5));
                }

                if (++n >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"tatoeba/sent-{++bn}", batch); n = 0; await Task.Yield();
                }
            }
            if (n > 0 && !options.DryRun) yield return b.Build();
        }

        // Pass 2: links → IS_TRANSLATION_OF between external-id entities.
        if (File.Exists(links))
        {
            var b = NewBuilder("tatoeba/link-0", batch);
            int n = 0, bn = 0;
            await foreach (var line in File.ReadLinesAsync(links, ct))
            {
                ct.ThrowIfCancellationRequested();
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                if (!long.TryParse(c[0], out long a) || !long.TryParse(c[1], out long bId)) continue;

                Hash128 ea = SourceEntityIdConventions.TatoebaSentence(a);
                Hash128 eb = SourceEntityIdConventions.TatoebaSentence(bId);
                // Emit endpoints inline (idempotent) so the FK holds even if a link
                // references an id absent from sentences.csv.
                b.AddEntity(new EntityRow(ea, /*tier*/ 3, SentenceRefTypeId, Source));
                b.AddEntity(new EntityRow(eb, /*tier*/ 3, SentenceRefTypeId, Source));
                b.AddAttestation(AttestationFactory.Create(
                    ea, KindIsTranslationOf, eb, Source, null,
                    KindValueTier.T6, TC.StructuredCorpusTier5));

                if (++n >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"tatoeba/link-{++bn}", batch); n = 0; await Task.Yield();
                }
            }
            if (n > 0 && !options.DryRun) yield return b.Build();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(13_262_153L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 8,
            physicalityCapacity: batch * 8,
            attestationCapacity: batch * 2);
}
