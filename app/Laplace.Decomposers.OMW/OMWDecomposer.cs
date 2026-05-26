using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

namespace Laplace.Decomposers.OMW;

/// <summary>
/// Emits Open Multilingual Wordnet (OMW) translation lemmas into the substrate.
/// For each `lemma` row in wn-data-{lang}.tab, emits a word entity and two
/// attestations: TRANSLATION_OF (lemma → WN synset) and HAS_LANGUAGE
/// (lemma → ISO 639-3 language entity). WN synset entities are guaranteed
/// to exist because WordNet (LayerOrder=2) runs before OMW (LayerOrder=3).
/// </summary>
public sealed class OMWDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    private static readonly Hash128 LemmaTypeId =
        Hash128.OfCanonical("substrate/type/OMW_Lemma/v1");

    // These kinds are used from the substrate bootstrap vocabulary (HAS_LANGUAGE,
    // IS_TRANSLATION_OF) — no need to re-register their entities.
    private static readonly Hash128 KindHasLanguage =
        Hash128.OfCanonical("substrate/kind/HAS_LANGUAGE/v1");
    private static readonly Hash128 KindIsTranslationOf =
        Hash128.OfCanonical("substrate/kind/IS_TRANSLATION_OF/v1");

    public Hash128 SourceId    => Source;
    public string  SourceName  => "OMWDecomposer";
    public int     LayerOrder  => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("OMW_Lemma");
        // HAS_LANGUAGE and IS_TRANSLATION_OF are substrate-canonical kinds
        // pre-seeded by 10_bootstrap.sql.in — no AddKind needed here.
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;

        var b = new SubstrateChangeBuilder(
            Source, "omw/batch-0", null,
            entityCapacity: batch, physicalityCapacity: 0, attestationCapacity: batch * 2);
        int count = 0, batchNum = 0;

        foreach (string langDir in Directory.GetDirectories(wnsDir))
        {
            string lang = Path.GetFileName(langDir);
            string tabFile = Path.Combine(langDir, $"wn-data-{lang}.tab");
            if (!File.Exists(tabFile)) continue;

            Hash128 langEntityId = LanguageEntityId.FromIso639_3(lang);

            await foreach (var entry in ParseFileAsync(tabFile, ct))
            {
                ct.ThrowIfCancellationRequested();

                Hash128 synsetId = SourceEntityIdConventions.WordNetSynset(
                    entry.SynsetOffset, entry.Pos);
                Hash128 lemmaId = LemmaId(entry.Lemma);

                b.AddEntity(lemmaId, /*tier*/ 2, LemmaTypeId, Source);
                b.AddAttestation(AttestationFactory.Create(
                    lemmaId, KindIsTranslationOf, synsetId, Source, null,
                    KindValueTier.T4, TC.StandardsDerivedTier2));
                b.AddAttestation(AttestationFactory.Create(
                    lemmaId, KindHasLanguage, langEntityId, Source, null,
                    KindValueTier.T4, TC.StandardsDerivedTier2));

                count++;
                if (count >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    batchNum++;
                    b = new SubstrateChangeBuilder(
                        Source, $"omw/batch-{batchNum}", null,
                        entityCapacity: batch, physicalityCapacity: 0,
                        attestationCapacity: batch * 2);
                    count = 0;
                    await Task.Yield();
                }
            }
        }

        if (count > 0 && !options.DryRun)
            yield return b.Build();
        await Task.Yield();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2_676_800L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Hash128 LemmaId(string lemma)
        => Hash128.OfCanonical($"word:{lemma.ToLowerInvariant()}");

    private static async IAsyncEnumerable<OmwEntry> ParseFileAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0 || line[0] == '#') continue;

            // Format: {id}-{pos}\t{[lang:]lemma|def|exe}\t{value}
            int t1 = line.IndexOf('\t');
            if (t1 < 0) continue;
            int t2 = line.IndexOf('\t', t1 + 1);
            if (t2 < 0) continue;

            string synKey = line[..t1];  // e.g. "00001740-n"
            string typeField = line[(t1 + 1)..t2];  // e.g. "als:lemma" or "lemma"
            string value = line[(t2 + 1)..];

            // Only process lemma rows
            int colonIdx = typeField.IndexOf(':');
            string entryType = colonIdx >= 0 ? typeField[(colonIdx + 1)..] : typeField;
            if (!entryType.Equals("lemma", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Parse synset key: {8digits}-{pos}
            int dashIdx = synKey.LastIndexOf('-');
            if (dashIdx < 0) continue;
            if (!long.TryParse(synKey[..dashIdx], out long offset)) continue;
            if (dashIdx + 1 >= synKey.Length) continue;
            char pos = synKey[dashIdx + 1];

            yield return new OmwEntry(offset, pos, value.Trim());
        }
    }

    private readonly record struct OmwEntry(long SynsetOffset, char Pos, string Lemma);
}
