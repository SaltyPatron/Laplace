using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

/// <summary>
/// Emits Universal Dependencies v2.17 as content + attestations.
///
/// The sentence text (CoNLL-U <c># text =</c>) is decomposed as content (so 2.6M
/// sentences become real, queryable, deduped content); each word form and lemma is
/// content-addressed (ContentEmitter) so it converges with WordNet/OMW/model/prompt.
/// Per token: HAS_UPOS, HAS_XPOS (language-specific POS), HAS_FEATURE (each
/// morphological feature), HAS_LANGUAGE, IS_LEMMA_OF, and a LABELLED dependency —
/// DEPENDS_ON head with <c>context_id</c> = the deprel entity (so the relation type,
/// e.g. nsubj / obj / amod, is queryable). Multi-word tokens (id "1-2") emit HAS_PART
/// to their split tokens. Empty nodes (id "8.1") are skipped (enhanced-deps refinement).
///
/// Abstract tags (UPOS/XPOS/feature/deprel) are external-id entities — emitted into the
/// batch so FK targets exist; the writer dedups within-batch + ON CONFLICT across batches.
/// </summary>
public sealed class UDDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 UposTypeId     = Hash128.OfCanonical("substrate/type/UD_UPOS/v1");
    private static readonly Hash128 XposTypeId     = Hash128.OfCanonical("substrate/type/UD_XPOS/v1");
    private static readonly Hash128 FeatureTypeId  = Hash128.OfCanonical("substrate/type/UD_Feature/v1");
    private static readonly Hash128 DeprelTypeId   = Hash128.OfCanonical("substrate/type/UD_Deprel/v1");
    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

    private static Hash128 Kind(string n) => Hash128.OfCanonical($"substrate/kind/{n}/v1");
    private static readonly Hash128 KindHasUpos     = Kind("HAS_UPOS");
    private static readonly Hash128 KindHasXpos     = Kind("HAS_XPOS");
    private static readonly Hash128 KindHasFeature  = Kind("HAS_FEATURE");
    private static readonly Hash128 KindIsLemmaOf   = Kind("IS_LEMMA_OF");
    private static readonly Hash128 KindDependsOn   = Kind("DEPENDS_ON");
    private static readonly Hash128 KindHasLanguage = Kind("HAS_LANGUAGE");
    private static readonly Hash128 KindHasPart     = Kind("HAS_PART");

    private static readonly string[] UposTags =
        ["ADJ","ADP","ADV","AUX","CCONJ","DET","INTJ","NOUN","NUM",
         "PART","PRON","PROPN","PUNCT","SCONJ","SYM","VERB","X"];

    public Hash128 SourceId     => Source;
    public string  SourceName   => "UDDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public Hash128 TrustClassId => TrustClass;

    private static Hash128 UposId(string t)  => Hash128.OfCanonical($"upos:{t}");
    // XPOS is treebank/language-specific (Penn "NN" ≠ another tagset's "NN"):
    // namespace by language so genuinely-different tags are distinct content.
    // UPOS is the universal tagset → stays unnamespaced (same content cross-lang).
    private static Hash128 XposId(string lang, string t) => Hash128.OfCanonical($"xpos:{lang}:{t}");
    private static Hash128 FeatId(string kv) => Hash128.OfCanonical($"feat:{kv}");
    private static Hash128 DeprelId(string d)=> Hash128.OfCanonical($"deprel:{d}");

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("UD_UPOS");
        boot.AddType("UD_XPOS");
        boot.AddType("UD_Feature");
        boot.AddType("UD_Deprel");
        boot.AddKind("HAS_UPOS",    KindRank.Partitive, SourceTrust.AcademicCurated);
        boot.AddKind("HAS_XPOS",    KindRank.Partitive, SourceTrust.AcademicCurated);
        boot.AddKind("HAS_FEATURE", KindRank.Partitive, SourceTrust.AcademicCurated);
        boot.AddKind("IS_LEMMA_OF", KindRank.Partitive, SourceTrust.AcademicCurated);
        boot.AddKind("DEPENDS_ON",  KindRank.Taxonomic, SourceTrust.AcademicCurated);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var upos = new SubstrateChangeBuilder(
            Source, "bootstrap/ud-upos", null,
            entityCapacity: UposTags.Length, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (var tag in UposTags)
            upos.AddEntity(new EntityRow(UposId(tag), 0, UposTypeId, Source));
        await context.Writer.ApplyAsync(upos.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir)) yield break;
        int batchSentences = options.BatchSize > 1 ? options.BatchSize : 256;

        var b = NewBuilder("ud/batch-0", batchSentences);
        int sentCount = 0, batchNum = 0;

        foreach (string conllu in Directory.EnumerateFiles(treebanksDir, "*.conllu", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string langCode = ExtractLangCode(Path.GetFileName(conllu));
            Hash128 langId = LanguageReference.Resolve(langCode);

            await foreach (var sentence in ParseSentencesAsync(conllu, ct))
            {
                ct.ThrowIfCancellationRequested();
                EmitSentence(b, sentence, langId, langCode);

                if (++sentCount >= batchSentences)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"ud/batch-{++batchNum}", batchSentences);
                    sentCount = 0;
                    await Task.Yield();
                }
            }
        }
        if (sentCount > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2_600_000L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChangeBuilder NewBuilder(string unit, int batchSentences) =>
        new(Source, unit, null,
            entityCapacity:      batchSentences * 40,
            physicalityCapacity: batchSentences * 40,
            attestationCapacity: batchSentences * 60);

    // ── emit ───────────────────────────────────────────────────────────────

    private static void EmitSentence(SubstrateChangeBuilder b, UdSentence s, Hash128 langId, string langCode)
    {
        // Language entity (idempotent with ISO layer) so HAS_LANGUAGE FK is satisfied.
        b.AddEntity(new EntityRow(langId, /*tier*/ 2, LanguageTypeId, Source));

        // The full sentence as content (the big win: 2.6M sentences become real content).
        if (!string.IsNullOrEmpty(s.Text)) ContentEmitter.Emit(b, s.Text!, Source);

        // Forms/lemmas as content; capture form content ids by token id for DEPENDS_ON.
        var formId = new Hash128?[s.MaxId + 1];
        foreach (var tok in s.Tokens)
        {
            formId[tok.Id] = ContentEmitter.Emit(b, tok.Form, Source);
            if (tok.Lemma != tok.Form) ContentEmitter.Emit(b, tok.Lemma, Source);
        }

        foreach (var tok in s.Tokens)
        {
            var fid = formId[tok.Id];
            if (fid is null) continue;
            Hash128 form = fid.Value;

            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                b.AddAttestation(AttestationFactory.Create(
                    form, KindHasUpos, UposId(tok.Upos), Source, null,
                    KindRank.Partitive, SourceTrust.AcademicCurated));

            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                b.AddEntity(new EntityRow(XposId(langCode, tok.Xpos), 0, XposTypeId, Source));
                b.AddAttestation(AttestationFactory.Create(
                    form, KindHasXpos, XposId(langCode, tok.Xpos), Source, null,
                    KindRank.Partitive, SourceTrust.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                b.AddEntity(new EntityRow(FeatId(feat), 0, FeatureTypeId, Source));
                b.AddAttestation(AttestationFactory.Create(
                    form, KindHasFeature, FeatId(feat), Source, null,
                    KindRank.Partitive, SourceTrust.AcademicCurated));
            }

            b.AddAttestation(AttestationFactory.Create(
                form, KindHasLanguage, langId, Source, null,
                KindRank.Partitive, SourceTrust.AcademicCurated));

            if (tok.Lemma != tok.Form)
            {
                var lemmaId = ContentEmitter.RootId(tok.Lemma);
                if (lemmaId is not null)
                    b.AddAttestation(AttestationFactory.Create(
                        lemmaId.Value, KindIsLemmaOf, form, Source, null,
                        KindRank.Partitive, SourceTrust.AcademicCurated));
            }

            // Labelled dependency: form DEPENDS_ON head, context_id = deprel entity.
            if (tok.Head > 0 && tok.Head <= s.MaxId && formId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                b.AddEntity(new EntityRow(DeprelId(tok.Deprel), 0, DeprelTypeId, Source));
                b.AddAttestation(AttestationFactory.Create(
                    form, KindDependsOn, headId, Source, DeprelId(tok.Deprel),
                    KindRank.Taxonomic, SourceTrust.AcademicCurated));
            }
        }

        // Multi-word tokens: surface form HAS_PART each split token's form.
        foreach (var mwt in s.Mwts)
        {
            var surfaceId = ContentEmitter.Emit(b, mwt.Form, Source);
            if (surfaceId is null) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (formId[id] is { } partId)
                    b.AddAttestation(AttestationFactory.Create(
                        surfaceId.Value, KindHasPart, partId, Source, null,
                        KindRank.Partitive, SourceTrust.AcademicCurated));
        }
    }

    private static string ExtractLangCode(string fileName)
    {
        int under = fileName.IndexOf('_');
        return under > 0 ? fileName[..under] : "und";
    }

    // ── CoNLL-U parser ───────────────────────────────────────────────────────

    private static async IAsyncEnumerable<UdSentence> ParseSentencesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var tokens = new List<UdToken>(48);
        var mwts = new List<UdMwt>(4);
        string? text = null;
        int maxId = 0;

        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrEmpty(line))
            {
                if (tokens.Count > 0)
                    yield return new UdSentence(text, tokens.ToList(), mwts.ToList(), maxId);
                tokens.Clear(); mwts.Clear(); text = null; maxId = 0;
                continue;
            }
            if (line[0] == '#')
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.AsSpan(0, eq).Trim().SequenceEqual("# text"))
                    text = line[(eq + 1)..].Trim();
                continue;
            }

            var c = line.Split('\t');
            if (c.Length < 8) continue;
            string id0 = c[0];

            if (id0.Contains('-'))   // multi-word token
            {
                int dash = id0.IndexOf('-');
                if (int.TryParse(id0[..dash], out int st) && int.TryParse(id0[(dash + 1)..], out int en))
                    mwts.Add(new UdMwt(st, en, c[1].Trim()));
                continue;
            }
            if (id0.Contains('.')) continue;            // empty node — skip
            if (!int.TryParse(id0, out int id)) continue;

            string form = c[1].Trim();
            if (form.Length == 0 || form == "_") continue;
            string lemma = c[2].Trim();
            if (lemma.Length == 0 || lemma == "_") lemma = form;
            string upos = c[3].Trim();
            string xpos = c[4].Trim();
            string[] feats = (c[5] == "_" || c[5].Length == 0)
                ? System.Array.Empty<string>()
                : c[5].Split('|', StringSplitOptions.RemoveEmptyEntries);
            int head = int.TryParse(c[6], out int h) ? h : 0;
            string deprel = c[7].Trim();

            if (id > maxId) maxId = id;
            tokens.Add(new UdToken(id, form, lemma, upos, xpos, feats, head, deprel));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(text, tokens.ToList(), mwts.ToList(), maxId);
    }

    private sealed record UdSentence(string? Text, List<UdToken> Tokens, List<UdMwt> Mwts, int MaxId);

    private readonly record struct UdToken(
        int Id, string Form, string Lemma, string Upos, string Xpos, string[] Feats, int Head, string Deprel);

    private readonly record struct UdMwt(int Start, int End, string Form);
}
