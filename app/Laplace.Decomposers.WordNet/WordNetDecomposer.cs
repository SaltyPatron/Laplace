using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.WordNet;

/// <summary>
/// Emits Princeton WordNet 3.0 into the substrate as "normal content + attestations".
///
/// Lemmas, glosses, and quoted example sentences are emitted as content-addressed
/// content (via <see cref="ContentEmitter"/> — same entity any other source produces
/// for the same text), NOT per-source string keys. Synsets / senses / POS / lexnames
/// are abstract WordNet constructs with no canonical text form, so they keep
/// content-addressed external-id identities.
///
/// Three passes (FK-safe): pass 1 emits entities (synsets, lemma/gloss/example content,
/// sense entities); pass 2 emits attestations. Data and index.sense are each streamed
/// once per pass.
///
/// Coverage: lemma↔synset membership; HAS_POS (n/v/a/s/r — adjective satellites kept
/// distinct); DEFINES (synset→gloss) + HAS_EXAMPLE (synset→quoted usage); HAS_DOMAIN_TOPIC
/// (synset→lexname DOMAIN wordform; the POS half rides HAS_POS — lexnames split); all ~25 pointer-symbol relations; senses from index.sense
/// with SemCor tag-count seeding the Glicko μ via <see cref="AttestationFactory.CreateWeighted"/>.
/// </summary>
public sealed class WordNetDecomposer : IDecomposer
{
    /// <summary>Meta-entity canonical names minted during parse — registered
    /// post-ingest so render() answers "wordnet/synset/n/1740", never hex
    /// (the Type:hex label fallback, 2026-06-05).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    // Entity type IDs (synset / sense / POS / lexname are abstract — not text content)
    private static readonly Hash128 SynsetTypeId      = Hash128.OfCanonical("substrate/type/WordNet_Synset/v1");
    private static readonly Hash128 SenseTypeId       = Hash128.OfCanonical("substrate/type/WordNet_Sense/v1");

    private static Hash128 Kind(string name) => Hash128.OfCanonical($"substrate/kind/{name}/v1");

    // Pointer symbol → kind NAME only. Subject = the synset bearing the pointer,
    // object = the target synset. WordNet pointer inventory per wninput(5).
    // Rank / symmetry / direction-flip resolve through RelationTypeRegistry (the single
    // source of truth for arena significance) at attest time — never locally.
    private static readonly Dictionary<string, string> PointerKinds = new()
    {
        ["!"]  = "IS_ANTONYM_OF",
        ["@"]  = "HAS_HYPERNYM",
        ["@i"] = "IS_INSTANCE_OF",
        ["~"]  = "HAS_HYPONYM",
        ["~i"] = "HAS_INSTANCE",
        ["#m"] = "IS_MEMBER_OF",
        ["#s"] = "IS_SUBSTANCE_OF",
        ["#p"] = "IS_PART_OF",
        ["%m"] = "HAS_MEMBER",
        ["%s"] = "HAS_SUBSTANCE",
        ["%p"] = "HAS_PART",
        ["="]  = "HAS_ATTRIBUTE",
        ["+"]  = "DERIVATIONALLY_RELATED",
        [";c"] = "HAS_DOMAIN_TOPIC",
        ["-c"] = "IS_DOMAIN_TOPIC_MEMBER",
        [";r"] = "HAS_DOMAIN_REGION",
        ["-r"] = "IS_DOMAIN_REGION_MEMBER",
        [";u"] = "HAS_DOMAIN_USAGE",
        ["-u"] = "IS_DOMAIN_USAGE_MEMBER",
        ["*"]  = "ENTAILS",
        [">"]  = "CAUSES",
        ["^"]  = "ALSO_SEE",
        ["$"]  = "IN_VERB_GROUP_WITH",
        ["&"]  = "IS_SIMILAR_TO",
        ["<"]  = "IS_PARTICIPLE_OF",
        ["\\"] = "PERTAINS_TO",
    };

    // lex_filenum (0..44) → lexicographer-file name, per WordNet lexnames.
    private static readonly string[] Lexnames =
    {
        "adj.all", "adj.pert", "adv.all", "noun.Tops", "noun.act", "noun.animal",
        "noun.artifact", "noun.attribute", "noun.body", "noun.cognition",
        "noun.communication", "noun.event", "noun.feeling", "noun.food", "noun.group",
        "noun.location", "noun.motive", "noun.object", "noun.person", "noun.phenomenon",
        "noun.plant", "noun.possession", "noun.process", "noun.quantity", "noun.relation",
        "noun.shape", "noun.state", "noun.substance", "noun.time", "verb.body",
        "verb.change", "verb.cognition", "verb.communication", "verb.competition",
        "verb.consumption", "verb.contact", "verb.creation", "verb.emotion", "verb.motion",
        "verb.perception", "verb.possession", "verb.social", "verb.stative", "verb.weather",
        "adj.ppl",
    };

    /* ss_type → THE canonical POS value (PosReference): n→NOUN v→VERB a/s→ADJ
     * r→ADV — satellite-ness stays on the synset id, never the POS value. */
    private static Hash128 PosId(char p) => PosReference.Resolve(p.ToString(), PosReference.PosTagset.WordNet);

    private const long EstimatedSynsets = 117_700L;

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WordNetDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private static readonly string[] PosFiles = ["data.noun", "data.verb", "data.adj", "data.adv"];

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("WordNet_Synset");
        boot.AddType("WordNet_Sense");
        boot.AddType("WordNet_POS");
        boot.AddType("WordNet_LexCategory");

        // Non-pointer kinds. Rank/symmetry live ONLY in RelationTypeRegistry; bootstrap
        // just guarantees the kind entities exist (SeedCanonical in Build() seeds
        // every canonical arena anyway — these cover source-named aliases).
        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_DOMAIN_TOPIC");
        boot.AddRelationType("HAS_VERB_FRAME");
        boot.AddRelationType("IS_LEMMA_OF");
        boot.AddRelationType("HAS_SENSE");
        boot.AddRelationType("IS_SENSE_OF");

        // All pointer-relation kinds (registry aliases resolve to canonical ids;
        // seeding the canonical entity is what matters for the FK floor).
        foreach (var name in PointerKinds.Values)
            boot.AddRelationType(RelationTypeRegistry.Resolve(name).Canonical);

        await context.Writer.ApplyAsync(boot.Build(), ct);

        // Seed THE canonical POS inventory (PosReference) + 45 lexname categories.
        var seed = new SubstrateChangeBuilder(
            Source, "bootstrap/wordnet-vocab", null,
            entityCapacity: PosReference.Canonical.Length + 1,
            physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(seed, Source);
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;

        // Pass 1: entities — synsets + lemma/gloss/example content + sense entities.
        await foreach (var change in StreamDataAsync(dictDir, batch, entitiesOnly: true, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
        await foreach (var change in StreamSensesAsync(dictDir, batch, entitiesOnly: true, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        // Pass 2: attestations — all synsets/lemmas/senses exist now.
        await foreach (var change in StreamDataAsync(dictDir, batch, entitiesOnly: false, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
        await foreach (var change in StreamSensesAsync(dictDir, batch, entitiesOnly: false, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        // Irregular inflections (.exc) — content + IS_LEMMA_OF, self-contained.
        await foreach (var change in StreamExceptionsAsync(dictDir, batch, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedSynsets);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => MetaNames.Keys.ToList();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── data.{noun,verb,adj,adv} streaming ───────────────────────────────────

    private static async IAsyncEnumerable<SubstrateChange> StreamDataAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"wordnet/data-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;
        var frameTemplates = LoadVerbFrames(dictDir);   // [1..35] templates; [0] unused

        foreach (var posFile in PosFiles)
        {
            string filePath = Path.Combine(dictDir, posFile);
            if (!File.Exists(filePath)) continue;

            await foreach (var syn in ParseDataAsync(filePath, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (entitiesOnly) EmitSynsetEntities(b, syn, frameTemplates);
                else              EmitSynsetAttestations(b, syn, frameTemplates);

                if (++count >= batch)
                {
                    yield return b.Build();
                    b = NewBuilder($"wordnet/data-{++batchNum}/{suffix}", entitiesOnly, batch);
                    count = 0;
                }
            }
        }
        if (count > 0) yield return b.Build();
    }

    private static void EmitSynsetEntities(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {
        b.AddEntity(syn.SynsetId, (byte)MetaTier.Meta, SynsetTypeId, Source);
        foreach (var lemma in syn.Lemmas)
            ContentEmitter.Emit(b, Surface(lemma), Source);

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0) ContentEmitter.Emit(b, def, Source);
        foreach (var ex in examples) ContentEmitter.Emit(b, ex, Source);

        // Lexname DOMAIN half ("animal" of noun.animal) as a wordform CONTENT
        // entity — the omni-glottal tie: the domain converges with every other
        // source that mentions the word (2026-06-05 lexname-split ruling).
        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            ContentEmitter.Emit(b, LexDomain(Lexnames[syn.LexFilenum]), Source);

        // Verb frame templates referenced by this synset, as content (FK for
        // the HAS_VERB_FRAME attestations in pass 2).
        foreach (var (frame, _) in syn.Frames)
            if (frame > 0 && frame < frameTemplates.Length && frameTemplates[frame] is { } tpl)
                ContentEmitter.Emit(b, tpl, Source);
    }

    /// <summary>The domain suffix of a lexname: noun.animal → "animal";
    /// noun.Tops → "Tops" (content is case-sensitive, emitted as-is).</summary>
    private static string LexDomain(string lexname)
    {
        int dot = lexname.IndexOf('.');
        return dot >= 0 ? lexname[(dot + 1)..] : lexname;
    }

    private static void EmitSynsetAttestations(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {
        Hash128 posId = PosId(syn.SsType);

        foreach (var lemma in syn.Lemmas)
        {
            var lemmaId = ContentEmitter.RootId(Surface(lemma));
            if (lemmaId is null) continue;
            b.AddAttestation(RelationTypeRegistry.Attest(
                lemmaId.Value, "IS_SYNONYM_OF", syn.SynsetId, Source, SourceTrust.StandardsDerived));
            b.AddAttestation(RelationTypeRegistry.Attest(
                lemmaId.Value, "HAS_POS", posId, Source, SourceTrust.StandardsDerived));
        }

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0)
        {
            var defId = ContentEmitter.RootId(def);
            if (defId is not null)
                b.AddAttestation(RelationTypeRegistry.Attest(
                    syn.SynsetId, "HAS_DEFINITION", defId.Value, Source, SourceTrust.StandardsDerived));
        }
        foreach (var ex in examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(RelationTypeRegistry.Attest(
                    syn.SynsetId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.StandardsDerived));
        }

        // Lexname SPLIT (2026-06-05): the POS half is already asserted per-lemma
        // via PosReference above; the DOMAIN half lands in the shared
        // HAS_DOMAIN_TOPIC arena (converges with Wiktionary categories) with
        // the domain WORDFORM as the value. HAS_LEX_CATEGORY (POS×domain
        // compound) is retired from emission.
        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
        {
            var domainId = ContentEmitter.RootId(LexDomain(Lexnames[syn.LexFilenum]));
            if (domainId is not null)
                b.AddAttestation(RelationTypeRegistry.Attest(
                    syn.SynsetId, "HAS_DOMAIN_TOPIC", domainId.Value,
                    Source, SourceTrust.StandardsDerived));
        }

        // Verb sentence frames (frames.vrb templates): w_num 00 → the synset
        // plays the frame; w_num k → that specific lemma's wordform does.
        foreach (var (frame, wordNum) in syn.Frames)
        {
            if (frame <= 0 || frame >= frameTemplates.Length || frameTemplates[frame] is not { } tpl) continue;
            var tplId = ContentEmitter.RootId(tpl);
            if (tplId is null) continue;
            Hash128 subject = syn.SynsetId;
            if (wordNum > 0 && wordNum <= syn.Lemmas.Count)
            {
                var lemmaId = ContentEmitter.RootId(Surface(syn.Lemmas[wordNum - 1]));
                if (lemmaId is { } lid) subject = lid;
            }
            b.AddAttestation(RelationTypeRegistry.Attest(
                subject, "HAS_VERB_FRAME", tplId.Value, Source, SourceTrust.StandardsDerived));
        }

        foreach (var ptr in syn.Pointers)
        {
            if (!PointerKinds.TryGetValue(ptr.Symbol, out var typeName)) continue;
            Hash128 tgt = SourceEntityIdConventions.WordNetSynset(ptr.TargetOffset, NormPos(ptr.TargetPos));
            // Registry resolves alias → canonical arena, applies the direction
            // flip (HAS_HYPONYM ⇒ IS_A with endpoints swapped) and symmetric
            // endpoint ordering, and supplies the canonical rank.
            b.AddAttestation(RelationTypeRegistry.Attest(
                syn.SynsetId, typeName, tgt, Source, SourceTrust.StandardsDerived));
        }
    }

    // ── index.sense streaming (senses + SemCor frequency) ────────────────────

    private static async IAsyncEnumerable<SubstrateChange> StreamSensesAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string path = Path.Combine(dictDir, "index.sense");
        if (!File.Exists(path)) yield break;

        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"wordnet/sense-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;

        await foreach (var s in ParseSensesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (entitiesOnly)
            {
                b.AddEntity(s.SenseId, /*tier*/ 2, SenseTypeId, Source);
                // The sense-key lemma is lowercase and may differ from the observed-case
                // data-file lemma, so the data pass may not have emitted this exact content
                // entity. Emit it here so the HAS_SENSE subject FK is always satisfied.
                ContentEmitter.Emit(b, s.Lemma, Source);
            }
            else
            {
                var lemmaId = ContentEmitter.RootId(s.Lemma);
                if (lemmaId is not null)
                {
                    // SemCor tag-count is the signed magnitude: a more-frequently-
                    // tagged sense wins harder (score = ½(1+tanh(count/M)), M = 1).
                    b.AddAttestation(RelationTypeRegistry.AttestWeighted(
                        lemmaId.Value, "HAS_SENSE", s.SenseId, Source, SourceTrust.StandardsDerived,
                        magnitude: s.TagCount, arenaScale: 1.0));
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        s.SenseId, "IS_SENSE_OF", s.SynsetId, Source, SourceTrust.StandardsDerived));
                }
            }

            if (++count >= batch)
            {
                yield return b.Build();
                b = NewBuilder($"wordnet/sense-{++batchNum}/{suffix}", entitiesOnly, batch);
                count = 0;
            }
        }
        if (count > 0) yield return b.Build();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SubstrateChangeBuilder NewBuilder(string unit, bool entitiesOnly, int batch) =>
        new(Source, unit, null,
            entityCapacity:      entitiesOnly ? batch * 6 : 0,
            physicalityCapacity: entitiesOnly ? batch * 6 : 0,
            attestationCapacity: entitiesOnly ? 0 : batch * 8);

    /// <summary>WordNet lemma surface: '_' marks a space in multi-word lemmas. Observed
    /// case — NO lowercasing (the content path is case-sensitive).</summary>
    private static string Surface(string lemma) => lemma.Replace('_', ' ');

    /// <summary>Adjective satellites ('s') share the adjective synset-id space ('a') so
    /// pointer targets and OMW cross-references resolve; satellite-ness is recorded only
    /// on the HAS_POS attestation.</summary>
    private static char NormPos(char ssType) => ssType == 's' ? 'a' : ssType;

    /// <summary>Split a WordNet gloss into its definition text and quoted example
    /// sentences. Examples are double-quoted spans; the definition is the remainder.</summary>
    /// <summary>frames.vrb: "N  Template text" — the 35 verb sentence frames.</summary>
    private static string?[] LoadVerbFrames(string dictDir)
    {
        var templates = new string?[40];
        string path = Path.Combine(dictDir, "frames.vrb");
        if (!File.Exists(path)) return templates;
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            int sp = t.IndexOf(' ');
            if (sp <= 0 || !int.TryParse(t[..sp], out int num)) continue;
            if (num > 0 && num < templates.Length) templates[num] = t[sp..].Trim();
        }
        return templates;
    }

    /// <summary>{noun,verb,adj,adv}.exc — irregular inflections ("went go"):
    /// base IS_LEMMA_OF inflected, the SAME direction UD emits, so the two
    /// sources co-assert on one lemma arena (2026-06-05 completeness).
    /// Self-contained batches: both wordforms + the attestation ride one
    /// intent (the writer orders entities before attestations).</summary>
    private static async IAsyncEnumerable<SubstrateChange> StreamExceptionsAsync(
        string dictDir, int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var b = NewBuilder("wordnet/exc-0", entitiesOnly: false, batch);
        int count = 0, batchNum = 0;
        foreach (var excFile in new[] { "noun.exc", "verb.exc", "adj.exc", "adv.exc" })
        {
            string path = Path.Combine(dictDir, excFile);
            if (!File.Exists(path)) continue;
            await foreach (var line in File.ReadLinesAsync(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string inflected = parts[0].Replace('_', ' ');
                var infId = ContentEmitter.Emit(b, inflected, Source);
                if (infId is null) continue;
                for (int i = 1; i < parts.Length; i++)
                {
                    string baseForm = parts[i].Replace('_', ' ');
                    var baseId = ContentEmitter.Emit(b, baseForm, Source);
                    if (baseId is null) continue;
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        baseId.Value, "IS_LEMMA_OF", infId.Value, Source, SourceTrust.StandardsDerived));
                }
                if (++count >= batch)
                {
                    yield return b.Build();
                    b = NewBuilder($"wordnet/exc-{++batchNum}", entitiesOnly: false, batch);
                    count = 0;
                }
            }
        }
        if (count > 0) yield return b.Build();
    }

    private static (string Def, List<string> Examples) ParseGloss(string gloss)
    {
        var examples = new List<string>();
        if (string.IsNullOrEmpty(gloss)) return ("", examples);
        var def = new System.Text.StringBuilder(gloss.Length);
        int i = 0;
        while (i < gloss.Length)
        {
            if (gloss[i] == '"')
            {
                int end = gloss.IndexOf('"', i + 1);
                if (end < 0) { def.Append(gloss.AsSpan(i)); break; }
                var ex = gloss[(i + 1)..end].Trim();
                if (ex.Length > 0) examples.Add(ex);
                i = end + 1;
            }
            else { def.Append(gloss[i]); i++; }
        }
        return (def.ToString().Trim().Trim(';', ' ').Trim(), examples);
    }

    // ── parsers ──────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<WnSynset> ParseDataAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0 || line[0] == ' ') continue; // header lines start with a space

            int glossSep = line.IndexOf(" | ", StringComparison.Ordinal);
            string synData = glossSep >= 0 ? line[..glossSep] : line;
            string gloss   = glossSep >= 0 ? line[(glossSep + 3)..] : "";

            var parts = synData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            if (!long.TryParse(parts[0], out long offset)) continue;       // synset_offset
            if (!int.TryParse(parts[1], out int lexFilenum)) lexFilenum = -1; // lex_filenum
            char ssType = parts[2].Length > 0 ? parts[2][0] : 'n';         // ss_type (n/v/a/s/r)
            if (!int.TryParse(parts[3], NumberStyles.HexNumber, null, out int wCnt)) continue;

            int idx = 4;
            var lemmas = new List<string>(wCnt);
            for (int w = 0; w < wCnt && idx + 1 < parts.Length; w++)
            {
                lemmas.Add(parts[idx]); // word
                idx += 2;               // skip lex_id
            }

            if (idx >= parts.Length || !int.TryParse(parts[idx++], out int pCnt)) continue;
            var pointers = new List<WnPointer>(pCnt);
            for (int p = 0; p < pCnt && idx + 3 < parts.Length; p++)
            {
                string sym = parts[idx++];
                if (!long.TryParse(parts[idx++], out long tgtOffset)) { idx += 2; continue; }
                char tgtPos = parts[idx++][0];
                idx++; // source/target word numbers (lexical-pointer precision — future)
                pointers.Add(new WnPointer(sym, tgtOffset, tgtPos));
            }

            // Verb sentence frames (data.verb only): "f_cnt  + f_num w_num"
            // repeated — the 35 syntactic templates of frames.vrb, previously
            // unparsed (2026-06-05 completeness). w_num 00 = every word in the
            // synset; >0 = that specific lemma (1-based).
            var frames = new List<(int Frame, int WordNum)>();
            if (idx < parts.Length && int.TryParse(parts[idx], out int fCnt) && fCnt > 0)
            {
                idx++;
                for (int f = 0; f < fCnt && idx + 2 < parts.Length + 1; f++)
                {
                    if (idx + 2 > parts.Length || parts[idx] != "+") break;
                    idx++;
                    if (!int.TryParse(parts[idx++], out int fNum)) break;
                    if (!int.TryParse(parts[idx++], NumberStyles.HexNumber, null, out int wNum)) break;
                    frames.Add((fNum, wNum));
                }
            }

            Hash128 synId = SourceEntityIdConventions.WordNetSynset(offset, NormPos(ssType));
            MetaNames.TryAdd($"wordnet/synset/{NormPos(ssType)}/{offset}", 0);
            yield return new WnSynset(synId, ssType, lexFilenum, lemmas, pointers, gloss, frames);
        }
    }

    private static async IAsyncEnumerable<WnSense> ParseSensesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            string senseKey = parts[0];
            if (!long.TryParse(parts[1], out long offset)) continue;
            if (!int.TryParse(parts[3], out int tagCount)) tagCount = 0;

            int pct = senseKey.IndexOf('%');
            if (pct <= 0 || pct + 1 >= senseKey.Length) continue;
            string lemma = senseKey[..pct].Replace('_', ' '); // observed case
            char pos = senseKey[pct + 1] switch
            {
                '1' => 'n', '2' => 'v', '3' => 'a', '4' => 'r', '5' => 's', _ => 'n',
            };

            Hash128 synId   = SourceEntityIdConventions.WordNetSynset(offset, NormPos(pos));
            Hash128 senseId = Hash128.OfCanonical($"wordnet/sense/{senseKey}");
            MetaNames.TryAdd($"wordnet/sense/{senseKey}", 0);
            yield return new WnSense(senseId, synId, lemma, tagCount);
        }
    }

    private sealed record WnSynset(
        Hash128 SynsetId, char SsType, int LexFilenum,
        List<string> Lemmas, List<WnPointer> Pointers, string Gloss,
        List<(int Frame, int WordNum)> Frames);

    private readonly record struct WnPointer(string Symbol, long TargetOffset, char TargetPos);

    private sealed record WnSense(Hash128 SenseId, Hash128 SynsetId, string Lemma, int TagCount);
}
