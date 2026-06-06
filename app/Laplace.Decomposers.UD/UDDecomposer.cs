using System.Collections.Concurrent;
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

    private static readonly Hash128 XposTypeId     = Hash128.OfCanonical("substrate/type/UD_XPOS/v1");
    private static readonly Hash128 FeatureTypeId  = Hash128.OfCanonical("substrate/type/UD_Feature/v1");
    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

    // POS value object id. UPOS is universal → unnamespaced; HAS_UPOS (the kind)
    // normalizes to HAS_POS via the registry so it co-asserts with other sources.

    private static readonly string[] UposTags =
        ["ADJ","ADP","ADV","AUX","CCONJ","DET","INTJ","NOUN","NUM",
         "PART","PRON","PROPN","PUNCT","SCONJ","SYM","VERB","X"];

    public Hash128 SourceId     => Source;
    public string  SourceName   => "UDDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public Hash128 TrustClassId => TrustClass;

    // Per-key id memos (the perf-cache discipline). XPOS tagsets and the feature
    // inventory are small, closed, low-cardinality sets, but XposId / FeatValueId
    // are on every token's hot path — without the memo each token re-formats +
    // UTF8-encodes + BLAKE3s the same string. Compute once per distinct key,
    // dictionary hit per token. Content-addressed ⇒ a hit is bit-identical.
    private static readonly ConcurrentDictionary<(string Lang, string Tag), Hash128> _xposIdMemo =
        new();
    private static readonly ConcurrentDictionary<(string Name, string Value), Hash128> _featValueIdMemo =
        new();

    /* UPOS value → THE canonical POS value entity (PosReference — the
     * omni-glottal POS resolution; same law as LanguageReference). The old
     * per-source `upos:{t}` ids forked the value layer three ways.
     * PosReference.Resolve memoizes its own id (CanonicalId memo). */
    private static Hash128 UposId(string t) => PosReference.Resolve(t, PosReference.PosTagset.Upos);
    // XPOS is treebank/language-specific (Penn "NN" ≠ another tagset's "NN"):
    // namespace by language so genuinely-different tags are distinct content.
    // UPOS is the universal tagset → stays unnamespaced (same content cross-lang).
    private static Hash128 XposId(string lang, string t) =>
        _xposIdMemo.GetOrAdd((lang, t), static k => Hash128.OfCanonical($"xpos:{k.Lang}:{k.Tag}"));
    // Feature VALUE object, namespaced by feature name (cross-language shared:
    // Number=Sing is one value entity). The feature TYPE (Number) is the kind.
    private static Hash128 FeatValueId(string name, string value) =>
        _featValueIdMemo.GetOrAdd((name, value), static k => Hash128.OfCanonical($"featval:{k.Name}:{k.Value}"));

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("UD_XPOS");
        boot.AddRelationType("HAS_DEFINITION");     // MISC Gloss=
        boot.AddRelationType("TRANSCRIBES_AS");     // MISC Translit=
        boot.AddRelationType("ENHANCED_DEPENDS_ON");
        boot.AddType("UD_Feature");
        // Kinds are seeded by the registry in BootstrapIntentBuilder.Build (the
        // canonical arena taxonomy: HAS_POS, HAS_XPOS, HAS_FEATURE, IS_LEMMA_OF,
        // DEPENDS_ON, …). Per-deprel / per-feature arenas (DEP_*, FEAT_*) are
        // dynamic, seeded on first sight in DecomposeAsync.
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var upos = new SubstrateChangeBuilder(
            Source, "bootstrap/ud-upos", null,
            entityCapacity: PosReference.Canonical.Length + 1, physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(upos, Source);
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

        // PARALLEL PRODUCER. UD is 200+ independent .conllu files and the
        // per-sentence decomposition (ContentEmitter → TextDecomposer +
        // HashComposer, all thread-safe: per-call native trees, concurrent
        // memos, read-only perf-cache) was the single-threaded floor of the
        // 2h CI job (measured 2,011s producer vs 2,597s apply on run
        // 27001038623). K workers each decompose whole files into their OWN
        // batches; intents merge through a bounded channel. Order-free by
        // construction: content-addressing makes intent order irrelevant, and
        // every batch is referentially SELF-CONTAINED (per-batch entity
        // seeding — see RelationTypeRegistry.SeedDynamic), so batches commit in any
        // order under parallel appliers too. Run-scoped taxonomy testimony
        // (IS_A, one witness statement per run) is gated by ONE shared
        // concurrent set across workers. LAPLACE_DECOMPOSE_WORKERS overrides;
        // 1 = the serial path below.
        int workers = int.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var w) && w > 0
            ? w : Math.Clamp(Environment.ProcessorCount - 2, 1, 4);

        var files = Directory.EnumerateFiles(treebanksDir, "*.conllu", SearchOption.AllDirectories).ToList();
        if (files.Count == 0) yield break;

        var seenAttRun = new ConcurrentIdSet();

        if (workers <= 1 || files.Count == 1)
        {
            var b = NewBuilder("ud/batch-0", batchSentences);
            var seenEntBatch = new HashSet<Hash128>();
            int sentCount = 0, batchNum = 0;
            foreach (string conllu in files)
            {
                ct.ThrowIfCancellationRequested();
                string langCode = ExtractLangCode(Path.GetFileName(conllu));
                Hash128 langId = LanguageReference.Resolve(langCode);

                await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun);

                    if (++sentCount >= batchSentences)
                    {
                        if (!options.DryRun) yield return b.Build();
                        b = NewBuilder($"ud/batch-{++batchNum}", batchSentences);
                        seenEntBatch.Clear();
                        sentCount = 0;
                        await Task.Yield();
                    }
                }
            }
            if (sentCount > 0 && !options.DryRun) yield return b.Build();
            yield break;
        }

        var fileQueue = new System.Collections.Concurrent.ConcurrentQueue<string>(files);
        var channel = System.Threading.Channels.Channel.CreateBounded<SubstrateChange>(
            new System.Threading.Channels.BoundedChannelOptions(workers * 4)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        var producers = new Task[workers];
        for (int wi = 0; wi < workers; wi++)
        {
            int worker = wi;
            producers[wi] = Task.Run(async () =>
            {
                while (fileQueue.TryDequeue(out var conllu))
                {
                    ct.ThrowIfCancellationRequested();
                    string langCode = ExtractLangCode(Path.GetFileName(conllu));
                    Hash128 langId = LanguageReference.Resolve(langCode);
                    string stem = Path.GetFileNameWithoutExtension(conllu);

                    var b = NewBuilder($"ud/w{worker}/{stem}/0", batchSentences);
                    var seenEntBatch = new HashSet<Hash128>();
                    int sentCount = 0, batchNum = 0;
                    await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                    {
                        EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun);
                        if (++sentCount >= batchSentences)
                        {
                            if (!options.DryRun) await channel.Writer.WriteAsync(b.Build(), ct);
                            b = NewBuilder($"ud/w{worker}/{stem}/{++batchNum}", batchSentences);
                            seenEntBatch.Clear();
                            sentCount = 0;
                        }
                    }
                    if (sentCount > 0 && !options.DryRun)
                        await channel.Writer.WriteAsync(b.Build(), ct);
                }
            }, ct);
        }

        // Completion propagates worker faults to the reader (no silent partial run).
        _ = Task.WhenAll(producers).ContinueWith(
            t => channel.Writer.TryComplete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var change in channel.Reader.ReadAllAsync(ct))
            yield return change;
        await Task.WhenAll(producers);   // surface any worker exception
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(30_000_000L);   // rows post-completeness (EDEP/MISC/empty nodes; 25.4M measured mid-run 2026-06-05)

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChangeBuilder NewBuilder(string unit, int batchSentences) =>
        new(Source, unit, null,
            entityCapacity:      batchSentences * 40,
            physicalityCapacity: batchSentences * 40,
            attestationCapacity: batchSentences * 60);

    // ── emit ───────────────────────────────────────────────────────────────

    private static void EmitSentence(SubstrateChangeBuilder b, UdSentence s, Hash128 langId, string langCode,
                                     HashSet<Hash128> seenEntBatch, ConcurrentIdSet seenAttRun)
    {
        // Language entity (idempotent with ISO layer) so HAS_LANGUAGE FK is satisfied.
        b.AddEntity(new EntityRow(langId, (byte)MetaTier.Meta, LanguageTypeId, Source));

        // The full sentence as content (the big win: 2.6M sentences become real content).
        if (!string.IsNullOrEmpty(s.Text)) ContentEmitter.Emit(b, s.Text!, Source);

        // Forms/lemmas as content; capture form content ids by token id for
        // DEPENDS_ON and by RAW ref ("8" / "8.1") for the ENHANCED graph,
        // which references empty nodes the basic graph never sees.
        var formId = new Hash128?[s.MaxId + 1];
        var refToForm = new Dictionary<string, Hash128>(s.Tokens.Count, StringComparer.Ordinal);
        foreach (var tok in s.Tokens)
        {
            var fid = ContentEmitter.Emit(b, tok.Form, Source);
            if (tok.Id >= 0) formId[tok.Id] = fid;
            if (fid is { } f) refToForm[tok.Ref] = f;
            if (tok.Lemma != tok.Form) ContentEmitter.Emit(b, tok.Lemma, Source);
        }

        foreach (var tok in s.Tokens)
        {
            if (!refToForm.TryGetValue(tok.Ref, out var form)) continue;

            // HAS_UPOS normalizes to the canonical HAS_POS arena (co-asserts with
            // WordNet/Wiktionary part-of-speech); the value object stays the UPOS tag.
            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                b.AddAttestation(RelationTypeRegistry.Attest(
                    form, "HAS_UPOS", UposId(tok.Upos), Source, SourceTrust.AcademicCurated));

            // HAS_XPOS is the finer, language-specific child arena (is_a HAS_POS).
            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                b.AddEntity(new EntityRow(XposId(langCode, tok.Xpos), (byte)MetaTier.Meta, XposTypeId, Source));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    form, "HAS_XPOS", XposId(langCode, tok.Xpos), Source, SourceTrust.AcademicCurated));
            }

            // Each morphological feature TYPE is its own arena: Number=Sing →
            // FEAT_NUMBER(form, Sing), is_a HAS_FEATURE. Value object shared cross-language.
            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                Hash128 valId = FeatValueId(fName, fVal);
                b.AddEntity(new EntityRow(valId, (byte)MetaTier.Meta, FeatureTypeId, Source));
                RelationTypeRegistry.SeedDynamic(b, RelationTypeRegistry.ResolveFeature(fName), Source, seenEntBatch, seenAttRun);
                b.AddAttestation(RelationTypeRegistry.AttestFeature(
                    form, fName, valId, Source, SourceTrust.AcademicCurated));
            }

            b.AddAttestation(RelationTypeRegistry.Attest(
                form, "HAS_LANGUAGE", langId, Source, SourceTrust.AcademicCurated));

            if (tok.Lemma != tok.Form)
            {
                var lemmaId = ContentEmitter.RootId(tok.Lemma);
                if (lemmaId is not null)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        lemmaId.Value, "IS_LEMMA_OF", form, Source, SourceTrust.AcademicCurated));
            }

            // Labelled dependency: the deprel IS the kind/arena (nsubj ≠ obj — each
            // its own embedding), form <DEP_*> head — NOT erased into context_id.
            // Seed the DEP_* kind + its is_a chain to DEPENDS_ON on first sight.
            if (tok.Head > 0 && tok.Head <= s.MaxId && formId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, Source, seenEntBatch, seenAttRun);
                b.AddAttestation(RelationTypeRegistry.AttestDeprel(
                    form, tok.Deprel, headId, Source, SourceTrust.AcademicCurated));
            }

            // ENHANCED dependencies (DEPS col 9 — "head:rel|head:rel", heads may
            // be empty nodes "8.1"): the EDEP_* family under ENHANCED_DEPENDS_ON —
            // a different annotation graph, never merged into DEP_* (2026-06-05).
            if (tok.Deps.Length > 0 && tok.Deps != "_")
            {
                foreach (var edge in tok.Deps.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = edge.IndexOf(':');
                    if (colon <= 0) continue;
                    string headRef = edge[..colon];
                    string erel = edge[(colon + 1)..].Trim();
                    if (erel.Length == 0 || headRef == "0") continue;
                    if (!refToForm.TryGetValue(headRef, out var eHead)) continue;
                    RelationTypeRegistry.SeedEnhancedDeprel(b, erel, Source, seenEntBatch, seenAttRun);
                    b.AddAttestation(RelationTypeRegistry.AttestEnhancedDeprel(
                        form, erel, eHead, Source, SourceTrust.AcademicCurated));
                }
            }

            // MISC col 10: Gloss= (the token's gloss/translation — a definition
            // witness) and Translit= (romanization — a transcription witness).
            if (tok.Misc.Length > 0 && tok.Misc != "_")
            {
                foreach (var kv in tok.Misc.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = kv[..eq];
                    string val = kv[(eq + 1)..].Trim();
                    if (val.Length == 0) continue;
                    if (key.Equals("Gloss", StringComparison.OrdinalIgnoreCase))
                    {
                        var g = ContentEmitter.Emit(b, val, Source);
                        if (g is { } gid)
                            b.AddAttestation(RelationTypeRegistry.Attest(
                                form, "HAS_DEFINITION", gid, Source, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = ContentEmitter.Emit(b, val, Source);
                        if (t is { } tid)
                            b.AddAttestation(RelationTypeRegistry.Attest(
                                form, "TRANSCRIBES_AS", tid, Source, SourceTrust.AcademicCurated));
                    }
                }
            }
        }

        // Multi-word tokens: surface form HAS_PART each split token's form.
        foreach (var mwt in s.Mwts)
        {
            var surfaceId = ContentEmitter.Emit(b, mwt.Form, Source);
            if (surfaceId is null) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (formId[id] is { } partId)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        surfaceId.Value, "HAS_PART", partId, Source, SourceTrust.AcademicCurated));
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
            // Empty nodes (id "8.1") are PARSED, not skipped (2026-06-05
            // completeness): they carry real morphology and are referenced as
            // heads by the ENHANCED dependency graph (DEPS col). They never
            // join the basic DEP graph (their HEAD col is "_" → 0 → skipped).
            bool isEmptyNode = id0.Contains('.');
            int id = 0;
            if (!isEmptyNode && !int.TryParse(id0, out id)) continue;

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
            string deps = c.Length > 8 ? c[8].Trim() : "_";
            string misc = c.Length > 9 ? c[9].Trim() : "_";

            if (!isEmptyNode && id > maxId) maxId = id;
            tokens.Add(new UdToken(isEmptyNode ? -1 : id, id0, form, lemma, upos, xpos, feats, head, deprel, deps, misc));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(text, tokens.ToList(), mwts.ToList(), maxId);
    }

    private sealed record UdSentence(string? Text, List<UdToken> Tokens, List<UdMwt> Mwts, int MaxId);

    private readonly record struct UdToken(
        int Id, string Ref, string Form, string Lemma, string Upos, string Xpos, string[] Feats,
        int Head, string Deprel, string Deps, string Misc);

    private readonly record struct UdMwt(int Start, int End, string Form);
}
