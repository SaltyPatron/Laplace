using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

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
/// distinct); DEFINES (synset→gloss) + HAS_EXAMPLE (synset→quoted usage); HAS_LEX_CATEGORY
/// (synset→one of 45 lexnames); all ~25 pointer-symbol relations; senses from index.sense
/// with SemCor tag-count seeding the Glicko μ via <see cref="AttestationFactory.CreateWeighted"/>.
/// </summary>
public sealed class WordNetDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    // Entity type IDs (synset / sense / POS / lexname are abstract — not text content)
    private static readonly Hash128 SynsetTypeId      = Hash128.OfCanonical("substrate/type/WordNet_Synset/v1");
    private static readonly Hash128 SenseTypeId       = Hash128.OfCanonical("substrate/type/WordNet_Sense/v1");
    private static readonly Hash128 PosTypeId         = Hash128.OfCanonical("substrate/type/WordNet_POS/v1");
    private static readonly Hash128 LexCategoryTypeId = Hash128.OfCanonical("substrate/type/WordNet_LexCategory/v1");

    private static Hash128 Kind(string name) => Hash128.OfCanonical($"substrate/kind/{name}/v1");

    // Non-pointer kinds
    private static readonly Hash128 KindIsSynonymOf   = Kind("IS_SYNONYM_OF");
    private static readonly Hash128 KindHasPos        = Kind("HAS_POS");
    private static readonly Hash128 KindDefines       = Kind("DEFINES");
    private static readonly Hash128 KindHasExample    = Kind("HAS_EXAMPLE");
    private static readonly Hash128 KindHasLexCat     = Kind("HAS_LEX_CATEGORY");
    private static readonly Hash128 KindHasSense      = Kind("HAS_SENSE");
    private static readonly Hash128 KindIsSenseOf     = Kind("IS_SENSE_OF");

    // Pointer symbol → (kind name, value tier). Subject = the synset bearing the pointer,
    // object = the target synset. WordNet pointer inventory per wninput(5).
    private static readonly Dictionary<string, (string Name, KindValueTier Tier)> PointerKinds = new()
    {
        ["!"]  = ("IS_ANTONYM_OF",           KindValueTier.T7),
        ["@"]  = ("HAS_HYPERNYM",            KindValueTier.T3),
        ["@i"] = ("IS_INSTANCE_OF",          KindValueTier.T3),
        ["~"]  = ("HAS_HYPONYM",             KindValueTier.T3),
        ["~i"] = ("HAS_INSTANCE",            KindValueTier.T3),
        ["#m"] = ("IS_MEMBER_OF",            KindValueTier.T4),
        ["#s"] = ("IS_SUBSTANCE_OF",         KindValueTier.T4),
        ["#p"] = ("IS_PART_OF",              KindValueTier.T4),
        ["%m"] = ("HAS_MEMBER",              KindValueTier.T4),
        ["%s"] = ("HAS_SUBSTANCE",           KindValueTier.T4),
        ["%p"] = ("HAS_PART",                KindValueTier.T4),
        ["="]  = ("HAS_ATTRIBUTE",           KindValueTier.T4),
        ["+"]  = ("DERIVATIONALLY_RELATED",  KindValueTier.T6),
        [";c"] = ("HAS_DOMAIN_TOPIC",        KindValueTier.T8),
        ["-c"] = ("IS_DOMAIN_TOPIC_MEMBER",  KindValueTier.T8),
        [";r"] = ("HAS_DOMAIN_REGION",       KindValueTier.T8),
        ["-r"] = ("IS_DOMAIN_REGION_MEMBER", KindValueTier.T8),
        [";u"] = ("HAS_DOMAIN_USAGE",        KindValueTier.T8),
        ["-u"] = ("IS_DOMAIN_USAGE_MEMBER",  KindValueTier.T8),
        ["*"]  = ("ENTAILS",                 KindValueTier.T5),
        [">"]  = ("CAUSES",                  KindValueTier.T5),
        ["^"]  = ("ALSO_SEE",                KindValueTier.T8),
        ["$"]  = ("IN_VERB_GROUP_WITH",      KindValueTier.T8),
        ["&"]  = ("IS_SIMILAR_TO",           KindValueTier.T6),
        ["<"]  = ("IS_PARTICIPLE_OF",        KindValueTier.T6),
        ["\\"] = ("PERTAINS_TO",             KindValueTier.T6),
    };

    // Precomputed kind ids for pointer symbols (avoid re-hashing per pointer).
    private static readonly Dictionary<string, Hash128> PointerKindId =
        PointerKinds.ToDictionary(kv => kv.Key, kv => Kind(kv.Value.Name));

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

    private static Hash128 PosId(char p)       => Hash128.OfCanonical($"wordnet/pos/{p}");
    private static Hash128 LexCatId(string nm) => Hash128.OfCanonical($"wordnet/lexname/{nm}");

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

        // Non-pointer kinds
        boot.AddKind("IS_SYNONYM_OF",    KindValueTier.T4, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_POS",          KindValueTier.T4, TC.StandardsDerivedTier2);
        boot.AddKind("DEFINES",          KindValueTier.T3, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_EXAMPLE",      KindValueTier.T4, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_LEX_CATEGORY", KindValueTier.T3, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_SENSE",        KindValueTier.T3, TC.StandardsDerivedTier2);
        boot.AddKind("IS_SENSE_OF",      KindValueTier.T3, TC.StandardsDerivedTier2);

        // All pointer-relation kinds
        foreach (var (_, (name, tier)) in PointerKinds)
            boot.AddKind(name, tier, TC.StandardsDerivedTier2);

        await context.Writer.ApplyAsync(boot.Build(), ct);

        // Seed the 5 POS entities (n/v/a/s/r — satellites distinct) + 45 lexname categories.
        var seed = new SubstrateChangeBuilder(
            Source, "bootstrap/wordnet-vocab", null,
            entityCapacity: 5 + Lexnames.Length, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (char p in new[] { 'n', 'v', 'a', 's', 'r' })
            seed.AddEntity(new EntityRow(PosId(p), 0, PosTypeId, Source));
        foreach (var nm in Lexnames)
            seed.AddEntity(new EntityRow(LexCatId(nm), 0, LexCategoryTypeId, Source));
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
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedSynsets);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── data.{noun,verb,adj,adv} streaming ───────────────────────────────────

    private static async IAsyncEnumerable<SubstrateChange> StreamDataAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"wordnet/data-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;

        foreach (var posFile in PosFiles)
        {
            string filePath = Path.Combine(dictDir, posFile);
            if (!File.Exists(filePath)) continue;

            await foreach (var syn in ParseDataAsync(filePath, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (entitiesOnly) EmitSynsetEntities(b, syn);
                else              EmitSynsetAttestations(b, syn);

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

    private static void EmitSynsetEntities(SubstrateChangeBuilder b, WnSynset syn)
    {
        b.AddEntity(syn.SynsetId, /*tier*/ 3, SynsetTypeId, Source);
        foreach (var lemma in syn.Lemmas)
            ContentEmitter.Emit(b, Surface(lemma), Source);

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0) ContentEmitter.Emit(b, def, Source);
        foreach (var ex in examples) ContentEmitter.Emit(b, ex, Source);
    }

    private static void EmitSynsetAttestations(SubstrateChangeBuilder b, WnSynset syn)
    {
        Hash128 posId = PosId(syn.SsType);

        foreach (var lemma in syn.Lemmas)
        {
            var lemmaId = ContentEmitter.RootId(Surface(lemma));
            if (lemmaId is null) continue;
            b.AddAttestation(AttestationFactory.Create(
                lemmaId.Value, KindIsSynonymOf, syn.SynsetId, Source, null,
                KindValueTier.T4, TC.StandardsDerivedTier2));
            b.AddAttestation(AttestationFactory.Create(
                lemmaId.Value, KindHasPos, posId, Source, null,
                KindValueTier.T4, TC.StandardsDerivedTier2));
        }

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0)
        {
            var defId = ContentEmitter.RootId(def);
            if (defId is not null)
                b.AddAttestation(AttestationFactory.Create(
                    syn.SynsetId, KindDefines, defId.Value, Source, null,
                    KindValueTier.T3, TC.StandardsDerivedTier2));
        }
        foreach (var ex in examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(AttestationFactory.Create(
                    syn.SynsetId, KindHasExample, exId.Value, Source, null,
                    KindValueTier.T4, TC.StandardsDerivedTier2));
        }

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            b.AddAttestation(AttestationFactory.Create(
                syn.SynsetId, KindHasLexCat, LexCatId(Lexnames[syn.LexFilenum]), Source, null,
                KindValueTier.T3, TC.StandardsDerivedTier2));

        foreach (var ptr in syn.Pointers)
        {
            if (!PointerKinds.TryGetValue(ptr.Symbol, out var pk)) continue;
            Hash128 tgt = SourceEntityIdConventions.WordNetSynset(ptr.TargetOffset, NormPos(ptr.TargetPos));
            b.AddAttestation(AttestationFactory.Create(
                syn.SynsetId, PointerKindId[ptr.Symbol], tgt, Source, null,
                pk.Tier, TC.StandardsDerivedTier2));
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
                    // SemCor tag-count seeds μ: a more-frequently-tagged sense gets a higher prior.
                    b.AddAttestation(AttestationFactory.CreateWeighted(
                        lemmaId.Value, KindHasSense, s.SenseId, Source, null,
                        KindValueTier.T3, TC.StandardsDerivedTier2,
                        magnitude: s.TagCount, floor: 1.0));
                    b.AddAttestation(AttestationFactory.Create(
                        s.SenseId, KindIsSenseOf, s.SynsetId, Source, null,
                        KindValueTier.T3, TC.StandardsDerivedTier2));
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
    /// case — NO lowercasing (the content path is case-sensitive per ADR 0047).</summary>
    private static string Surface(string lemma) => lemma.Replace('_', ' ');

    /// <summary>Adjective satellites ('s') share the adjective synset-id space ('a') so
    /// pointer targets and OMW cross-references resolve; satellite-ness is recorded only
    /// on the HAS_POS attestation.</summary>
    private static char NormPos(char ssType) => ssType == 's' ? 'a' : ssType;

    /// <summary>Split a WordNet gloss into its definition text and quoted example
    /// sentences. Examples are double-quoted spans; the definition is the remainder.</summary>
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

            Hash128 synId = SourceEntityIdConventions.WordNetSynset(offset, NormPos(ssType));
            yield return new WnSynset(synId, ssType, lexFilenum, lemmas, pointers, gloss);
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
            yield return new WnSense(senseId, synId, lemma, tagCount);
        }
    }

    private sealed record WnSynset(
        Hash128 SynsetId, char SsType, int LexFilenum,
        List<string> Lemmas, List<WnPointer> Pointers, string Gloss);

    private readonly record struct WnPointer(string Symbol, long TargetOffset, char TargetPos);

    private sealed record WnSense(Hash128 SenseId, Hash128 SynsetId, string Lemma, int TagCount);
}
