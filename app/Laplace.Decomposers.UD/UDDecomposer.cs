using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

namespace Laplace.Decomposers.UD;

/// <summary>
/// Emits Universal Dependencies v2.17 word-form and lemma entities plus
/// syntactic attestations. For each token: HAS_UPOS, IS_LEMMA_OF,
/// HAS_LANGUAGE, and DEPENDS_ON (when head != 0). Sentences are batched
/// across all .conllu files under ud-treebanks-v2.17/.
///
/// Language code mapping: the filename prefix (ISO 639-1 or ISO 639-3)
/// is used directly via <see cref="LanguageEntityId.FromIso639_3"/>. The
/// language entity is emitted in each batch so the HAS_LANGUAGE FK is
/// always satisfied, even for codes that differ from ISO 639-3 three-
/// letter codes. On re-ingest or overlap with ISODecomposer the writer's
/// within-batch dedup handles it.
/// </summary>
public sealed class UDDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 FormTypeId =
        Hash128.OfCanonical("substrate/type/UD_WordForm/v1");
    private static readonly Hash128 LemmaTypeId =
        Hash128.OfCanonical("substrate/type/UD_Lemma/v1");
    private static readonly Hash128 UposTypeId =
        Hash128.OfCanonical("substrate/type/UD_UPOS/v1");
    private static readonly Hash128 LanguageTypeId =
        Hash128.OfCanonical("substrate/type/Language/v1");

    private static readonly Hash128 KindHasUpos =
        Hash128.OfCanonical("substrate/kind/HAS_UPOS/v1");
    private static readonly Hash128 KindIsLemmaOf =
        Hash128.OfCanonical("substrate/kind/IS_LEMMA_OF/v1");
    private static readonly Hash128 KindDependsOn =
        Hash128.OfCanonical("substrate/kind/DEPENDS_ON/v1");
    private static readonly Hash128 KindHasLanguage =
        Hash128.OfCanonical("substrate/kind/HAS_LANGUAGE/v1");

    // Universal POS tags — seeded in InitializeAsync
    private static readonly string[] UposhTags =
        ["ADJ","ADP","ADV","AUX","CCONJ","DET","INTJ","NOUN","NUM",
         "PART","PRON","PROPN","PUNCT","SCONJ","SYM","VERB","X"];

    public Hash128 SourceId    => Source;
    public string  SourceName  => "UDDecomposer";
    public int     LayerOrder  => 4;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("UD_WordForm");
        boot.AddType("UD_Lemma");
        boot.AddType("UD_UPOS");
        boot.AddKind("HAS_UPOS",     KindValueTier.T4, TC.AcademicCuratedTier3);
        boot.AddKind("IS_LEMMA_OF",  KindValueTier.T4, TC.AcademicCuratedTier3);
        boot.AddKind("DEPENDS_ON",   KindValueTier.T3, TC.AcademicCuratedTier3);
        boot.AddKind("HAS_LANGUAGE", KindValueTier.T4, TC.AcademicCuratedTier3);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        // Seed UPOS tag entities
        var upos = new SubstrateChangeBuilder(
            Source, "bootstrap/ud-upos", null,
            entityCapacity: UposhTags.Length, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (var tag in UposhTags)
            upos.AddEntity(UposEntityId(tag), 0, UposTypeId, Source);
        await context.Writer.ApplyAsync(upos.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        int batchSentences = options.BatchSize > 1 ? options.BatchSize : 512;

        var b = new SubstrateChangeBuilder(
            Source, "ud/batch-0", null,
            entityCapacity: batchSentences * 20,
            physicalityCapacity: 0,
            attestationCapacity: batchSentences * 40);
        int sentCount = 0, batchNum = 0;

        foreach (string conllu in Directory.EnumerateFiles(treebanksDir, "*.conllu",
                     SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string langCode = ExtractLangCode(Path.GetFileName(conllu));
            Hash128 langId = LanguageEntityId.FromIso639_3(langCode);
            // Emit language entity so HAS_LANGUAGE FK is always satisfied
            b.AddEntity(langId, /*tier*/ 2, LanguageTypeId, Source);

            await foreach (var sentence in ParseSentencesAsync(conllu, ct))
            {
                ct.ThrowIfCancellationRequested();
                EmitSentence(b, sentence, langId);
                sentCount++;

                if (sentCount >= batchSentences)
                {
                    if (!options.DryRun) yield return b.Build();
                    batchNum++;
                    b = new SubstrateChangeBuilder(
                        Source, $"ud/batch-{batchNum}", null,
                        entityCapacity: batchSentences * 20,
                        physicalityCapacity: 0,
                        attestationCapacity: batchSentences * 40);
                    sentCount = 0;
                    await Task.Yield();
                }
            }
        }

        if (sentCount > 0 && !options.DryRun)
            yield return b.Build();
        await Task.Yield();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2_600_000L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── emit ───────────────────────────────────────────────────────────────

    private static void EmitSentence(
        SubstrateChangeBuilder b, UdSentence sentence, Hash128 langId)
    {
        // Build token-index → entity-id map for DEPENDS_ON references
        var tokenIds = new Hash128[sentence.Tokens.Count + 1]; // 1-indexed

        // Pass 1: register form/lemma entities
        for (int i = 0; i < sentence.Tokens.Count; i++)
        {
            var tok = sentence.Tokens[i];
            Hash128 formId  = WordEntityId(tok.Form);
            Hash128 lemmaId = WordEntityId(tok.Lemma);
            tokenIds[tok.Id] = formId;

            b.AddEntity(formId,  /*tier*/ 2, FormTypeId,  Source);
            b.AddEntity(lemmaId, /*tier*/ 2, LemmaTypeId, Source);
        }

        // Pass 2: attestations (all entities now recorded in builder)
        foreach (var tok in sentence.Tokens)
        {
            Hash128 formId  = WordEntityId(tok.Form);
            Hash128 lemmaId = WordEntityId(tok.Lemma);

            // IS_LEMMA_OF: lemma → form
            if (tok.Lemma != tok.Form)
                b.AddAttestation(AttestationFactory.Create(
                    lemmaId, KindIsLemmaOf, formId, Source, null,
                    KindValueTier.T4, TC.AcademicCuratedTier3));

            // HAS_UPOS: form → UPOS entity
            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                b.AddAttestation(AttestationFactory.Create(
                    formId, KindHasUpos, UposEntityId(tok.Upos), Source, null,
                    KindValueTier.T4, TC.AcademicCuratedTier3));

            // HAS_LANGUAGE: form → language entity
            b.AddAttestation(AttestationFactory.Create(
                formId, KindHasLanguage, langId, Source, null,
                KindValueTier.T4, TC.AcademicCuratedTier3));

            // DEPENDS_ON: form → head form (when head != 0 = root)
            if (tok.Head > 0 && tok.Head < tokenIds.Length && tokenIds[tok.Head] != default)
                b.AddAttestation(AttestationFactory.Create(
                    formId, KindDependsOn, tokenIds[tok.Head], Source, null,
                    KindValueTier.T3, TC.AcademicCuratedTier3));
        }
    }

    private static Hash128 WordEntityId(string text)
        => Hash128.OfCanonical($"word:{text.ToLowerInvariant()}");

    private static Hash128 UposEntityId(string tag)
        => Hash128.OfCanonical($"upos:{tag}");

    private static string ExtractLangCode(string fileName)
    {
        // af_afribooms-ud-dev.conllu → "af"
        int under = fileName.IndexOf('_');
        return under > 0 ? fileName[..under] : "und";
    }

    // ── CoNLL-U parser ──────────────────────────────────────────────────────

    private static async IAsyncEnumerable<UdSentence> ParseSentencesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tokens = new List<UdToken>(32);
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrEmpty(line))
            {
                if (tokens.Count > 0)
                {
                    yield return new UdSentence(tokens.ToList());
                    tokens.Clear();
                }
                continue;
            }
            if (line[0] == '#') continue; // comment

            var parts = line.Split('\t');
            if (parts.Length < 8) continue;
            // Skip multi-word tokens (id contains '-') and empty nodes ('.')
            if (parts[0].Contains('-') || parts[0].Contains('.')) continue;
            if (!int.TryParse(parts[0], out int id)) continue;

            string form   = parts[1].Trim();
            string lemma  = parts[2].Trim();
            string upos   = parts[3].Trim();
            // parts[4] = xpos (skip)
            // parts[5] = feats (skip for now)
            int head = int.TryParse(parts[6], out int h) ? h : 0;
            // parts[7] = deprel (skip context for now)

            if (string.IsNullOrEmpty(form) || form == "_") continue;
            if (string.IsNullOrEmpty(lemma) || lemma == "_") lemma = form;

            tokens.Add(new UdToken(id, form, lemma, upos, head));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(tokens.ToList());
    }

    private sealed record UdSentence(List<UdToken> Tokens);

    private readonly record struct UdToken(
        int Id, string Form, string Lemma, string Upos, int Head);
}
