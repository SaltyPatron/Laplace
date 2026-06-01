using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

/// <summary>
/// Emits Open Multilingual Wordnet (OMW) into the substrate as content + attestations.
///
/// Every wn-data-&lt;lang&gt;.tab row type is captured: lemma (→ IS_TRANSLATION_OF synset,
/// HAS_LANGUAGE), def (→ synset DEFINES gloss, context_id = lang), exe (→ synset HAS_EXAMPLE,
/// context_id = lang). Lemmas/defs/examples are content-addressed via ContentEmitter so they
/// converge with WordNet/model/prompt. WN synsets come from the WordNet layer (LayerOrder=2)
/// which runs before OMW (LayerOrder=3); ISO languages (LayerOrder=1) likewise.
///
/// SINGLE pass: each row emits its content entity AND the attestation that references it in the
/// SAME intent. The writer orders entities before attestations within an intent, so the FK is
/// satisfied without a second decode of the text (the prior two-pass version decoded twice).
/// </summary>
public sealed class OMWDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");
    private static readonly Hash128 SynsetTypeId   = Hash128.OfCanonical("substrate/type/WordNet_Synset/v1");

    private static Hash128 Kind(string n) => Hash128.OfCanonical($"substrate/kind/{n}/v1");
    private static readonly Hash128 KindHasLanguage     = Kind("HAS_LANGUAGE");
    private static readonly Hash128 KindIsTranslationOf = Kind("IS_TRANSLATION_OF");
    private static readonly Hash128 KindDefines         = Kind("DEFINES");
    private static readonly Hash128 KindHasExample      = Kind("HAS_EXAMPLE");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OMWDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        // IS_TRANSLATION_OF / HAS_LANGUAGE are substrate-canonical (bootstrap); DEFINES /
        // HAS_EXAMPLE are registered here (idempotent — content-addressed by name).
        boot.AddKind("DEFINES",     KindRank.Taxonomic, SourceTrust.AcademicCurated);
        boot.AddKind("HAS_EXAMPLE", KindRank.Partitive, SourceTrust.AcademicCurated);
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        var b = NewBuilder("omw/batch-0", batch);
        int count = 0, batchNum = 0;

        // All wn-data-*.tab anywhere under wns/ (dirs like mcr/, msa/, cow/ hold files whose
        // lang differs from the dir name) — the file's lang is the fallback; a row's own
        // lang prefix wins.
        foreach (string tabFile in Directory.EnumerateFiles(wnsDir, "wn-data-*.tab", SearchOption.AllDirectories))
        {
            string fileLang = FileLang(tabFile);
            await foreach (var row in ParseFileAsync(tabFile, fileLang, ct))
            {
                ct.ThrowIfCancellationRequested();
                EmitRow(b, row);
                if (++count >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"omw/batch-{++batchNum}", batch);
                    count = 0;
                    await Task.Yield();
                }
            }
        }
        if (count > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2_676_800L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EmitRow(SubstrateChangeBuilder b, OmwRow row)
    {
        var contentId = ContentEmitter.Emit(b, row.Value, Source);
        if (contentId is null) return;

        Hash128 langId = LanguageReference.Resolve(row.Lang);
        b.AddEntity(new EntityRow(langId, /*tier*/ 2, LanguageTypeId, Source));
        // Defensive: the referenced WN synset normally exists from the WordNet layer; emit it
        // here too (ON CONFLICT keeps WordNet's row) so a synset OMW references but WordNet
        // lacks can't FK-crash the batch.
        b.AddEntity(new EntityRow(row.SynsetId, /*tier*/ 3, SynsetTypeId, Source));

        switch (row.Type)
        {
            case OmwType.Lemma:
                b.AddAttestation(AttestationFactory.Create(
                    contentId.Value, KindIsTranslationOf, row.SynsetId, Source, null,
                    KindRank.Partitive, SourceTrust.AcademicCurated));
                b.AddAttestation(AttestationFactory.Create(
                    contentId.Value, KindHasLanguage, langId, Source, null,
                    KindRank.Partitive, SourceTrust.AcademicCurated));
                break;
            case OmwType.Def:
                // context_id = language → per-language glosses are distinct attestations.
                b.AddAttestation(AttestationFactory.Create(
                    row.SynsetId, KindDefines, contentId.Value, Source, langId,
                    KindRank.Taxonomic, SourceTrust.AcademicCurated));
                break;
            case OmwType.Exe:
                b.AddAttestation(AttestationFactory.Create(
                    row.SynsetId, KindHasExample, contentId.Value, Source, langId,
                    KindRank.Partitive, SourceTrust.AcademicCurated));
                break;
        }
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 6,
            physicalityCapacity: batch * 6,
            attestationCapacity: batch * 2);

    private static string FileLang(string path)
    {
        // wn-data-<lang>.tab → <lang>
        string name = Path.GetFileNameWithoutExtension(path); // wn-data-<lang>
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }

    private static async IAsyncEnumerable<OmwRow> ParseFileAsync(
        string path, string fileLang, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0 || line[0] == '#') continue;

            var cols = line.Split('\t');
            if (cols.Length < 3) continue;

            string synKey = cols[0];          // e.g. 00001740-n
            string typeField = cols[1];       // lemma | eng:lemma | jpn:def | arb:lemma:root

            // Parse [lang:]type[:variant]
            string lang = fileLang;
            string type;
            var tf = typeField.Split(':');
            if (tf.Length == 1) { type = tf[0]; }
            else { lang = tf[0]; type = tf[1]; }

            OmwType kind;
            string value;
            switch (type)
            {
                case "lemma": kind = OmwType.Lemma; value = cols.Length > 2 ? cols[2] : ""; break;
                case "def":   kind = OmwType.Def;   value = cols.Length > 3 ? cols[3] : (cols.Length > 2 ? cols[2] : ""); break;
                case "exe":   kind = OmwType.Exe;   value = cols.Length > 3 ? cols[3] : (cols.Length > 2 ? cols[2] : ""); break;
                default: continue;
            }
            value = value.Replace('_', ' ').Trim(); // '_' → space; observed case
            if (value.Length == 0) continue;

            int dash = synKey.LastIndexOf('-');
            if (dash < 0 || dash + 1 >= synKey.Length) continue;
            if (!long.TryParse(synKey[..dash], out long offset)) continue;
            char pos = synKey[dash + 1] == 's' ? 'a' : synKey[dash + 1]; // satellites share 'a' (match WordNet)

            Hash128 synId = SourceEntityIdConventions.WordNetSynset(offset, pos);
            yield return new OmwRow(synId, lang, kind, value);
        }
    }

    private enum OmwType { Lemma, Def, Exe }

    private readonly record struct OmwRow(Hash128 SynsetId, string Lang, OmwType Type, string Value);
}
