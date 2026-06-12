using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.WordNet;

public sealed class WordNetDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.StrictSerial;

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;
    private static readonly Hash128 SenseTypeId  = EntityTypeRegistry.WordNetSense;

    private static readonly Dictionary<string, string> PointerTypes = new()
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

        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_DOMAIN_TOPIC");
        boot.AddRelationType("HAS_VERB_FRAME");
        boot.AddRelationType("IS_LEMMA_OF");
        boot.AddRelationType("HAS_SENSE");
        boot.AddRelationType("IS_SENSE_OF");

        foreach (var name in PointerTypes.Values)
            boot.AddRelationType(RelationTypeRegistry.Resolve(name).Canonical);

        await context.Writer.ApplyAsync(boot.Build(), ct);

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

        await foreach (var change in StreamDataAsync(dictDir, batch, entitiesOnly: true, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
        await foreach (var change in StreamSensesAsync(dictDir, batch, entitiesOnly: true, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        await foreach (var change in StreamDataAsync(dictDir, batch, entitiesOnly: false, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
        await foreach (var change in StreamSensesAsync(dictDir, batch, entitiesOnly: false, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        await foreach (var change in StreamExceptionsAsync(dictDir, batch, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        var files = new List<IngestFileSpec>();
        foreach (var pos in PosFiles)
        {
            string p = Path.Combine(dictDir, pos);
            if (File.Exists(p))
                files.Add(new(pos, p, CountSynsetLines(p)));
        }
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return Task.FromResult<IngestInventory?>(
            files.Count > 0 ? new IngestInventory("synsets", total, files) : null);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits ?? EstimatedSynsets;
    }

    private static long CountSynsetLines(string path)
    {
        long n = 0;
        foreach (var line in File.ReadLines(path))
            if (line.Length > 0 && char.IsDigit(line[0])) n++;
        return n;
    }

    public IReadOnlyCollection<string> CanonicalNamesForReadback => MetaNames.Keys.ToList();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<SubstrateChange> StreamDataAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"wordnet/data-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;
        var frameTemplates = LoadVerbFrames(dictDir);

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
                    yield return b.SetInputUnitsConsumed(count).Build();
                    b = NewBuilder($"wordnet/data-{++batchNum}/{suffix}", entitiesOnly, batch);
                    count = 0;
                }
            }
        }
        if (count > 0) yield return b.SetInputUnitsConsumed(count).Build();
    }

    private static void EmitSynsetEntities(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {
        b.AddEntity(syn.SynsetId, EntityTier.Vocabulary, SynsetTypeId, Source);
        foreach (var lemma in syn.Lemmas)
            ContentEmitter.Emit(b, Surface(lemma), Source);

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0) ContentEmitter.Emit(b, def, Source);
        foreach (var ex in examples) ContentEmitter.Emit(b, ex, Source);

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            ContentEmitter.Emit(b, LexDomain(Lexnames[syn.LexFilenum]), Source);

        foreach (var (frame, _) in syn.Frames)
            if (frame > 0 && frame < frameTemplates.Length && frameTemplates[frame] is { } tpl)
                ContentEmitter.Emit(b, tpl, Source);
    }

    private static string LexDomain(string lexname)
    {
        int dot = lexname.IndexOf('.');
        return dot >= 0 ? lexname[(dot + 1)..] : lexname;
    }

    // RootId here is LAWFUL re-derivation, not a ghost reference: every surface
    // this pass attests to was Emitted by EmitSynsetEntities in pass 1 (the
    // tier-witness law's two-pass shape — witness first, then claim).
    private static void EmitSynsetAttestations(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {
        Hash128 posId = PosId(syn.SsType);

        foreach (var lemma in syn.Lemmas)
        {
            var lemmaId = ContentEmitter.RootId(Surface(lemma));
            if (lemmaId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "IS_SYNONYM_OF", syn.SynsetId, Source, SourceTrust.StandardsDerived));
            b.AddAttestation(NativeAttestation.PosWordNet(
                lemmaId.Value, syn.SsType, Source, null, SourceTrust.StandardsDerived));
        }

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0)
        {
            var defId = ContentEmitter.RootId(def);
            if (defId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    syn.SynsetId, "HAS_DEFINITION", defId.Value, Source, SourceTrust.StandardsDerived));
        }
        foreach (var ex in examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    syn.SynsetId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.StandardsDerived));
        }

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
        {
            var domainId = ContentEmitter.RootId(LexDomain(Lexnames[syn.LexFilenum]));
            if (domainId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    syn.SynsetId, "HAS_DOMAIN_TOPIC", domainId.Value,
                    Source, SourceTrust.StandardsDerived));
        }

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
            b.AddAttestation(NativeAttestation.Categorical(
                subject, "HAS_VERB_FRAME", tplId.Value, Source, SourceTrust.StandardsDerived));
        }

        foreach (var ptr in syn.Pointers)
        {
            if (!PointerTypes.TryGetValue(ptr.Symbol, out var typeName)) continue;
            Hash128 tgt = SourceEntityIdConventions.WordNetSynset(ptr.TargetOffset, NormPos(ptr.TargetPos));
            b.AddAttestation(NativeAttestation.Categorical(
                syn.SynsetId, typeName, tgt, Source, SourceTrust.StandardsDerived));
        }
    }

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
                b.AddEntity(s.SenseId, 2, SenseTypeId, Source);
                ContentEmitter.Emit(b, s.Lemma, Source);
            }
            else
            {
                var lemmaId = ContentEmitter.RootId(s.Lemma);
                if (lemmaId is not null)
                {
                    b.AddAttestation(NativeAttestation.Categorical(
                        lemmaId.Value, "HAS_SENSE", s.SenseId, Source, SourceTrust.StandardsDerived,
                        magnitude: s.TagCount, arenaScale: 1.0));
                    b.AddAttestation(NativeAttestation.Categorical(
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

    private static SubstrateChangeBuilder NewBuilder(string unit, bool entitiesOnly, int batch) =>
        new(Source, unit, null,
            entityCapacity:      entitiesOnly ? batch * 6 : 0,
            physicalityCapacity: entitiesOnly ? batch * 6 : 0,
            attestationCapacity: entitiesOnly ? 0 : batch * 8);

    private static string Surface(string lemma) => lemma.Replace('_', ' ');

    private static char NormPos(char ssType) => ssType == 's' ? 'a' : ssType;

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
            await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                string line = System.Text.Encoding.UTF8.GetString(lineMem.Span);
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
                    b.AddAttestation(NativeAttestation.Categorical(
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

    private static async IAsyncEnumerable<WnSynset> ParseDataAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = System.Text.Encoding.UTF8.GetString(lineMem.Span);
            if (line.Length == 0 || line[0] == ' ') continue;

            int glossSep = line.IndexOf(" | ", StringComparison.Ordinal);
            string synData = glossSep >= 0 ? line[..glossSep] : line;
            string gloss   = glossSep >= 0 ? line[(glossSep + 3)..] : "";

            var parts = synData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            if (!long.TryParse(parts[0], out long offset)) continue;
            if (!int.TryParse(parts[1], out int lexFilenum)) lexFilenum = -1;
            char ssType = parts[2].Length > 0 ? parts[2][0] : 'n';
            if (!int.TryParse(parts[3], NumberStyles.HexNumber, null, out int wCnt)) continue;

            int idx = 4;
            var lemmas = new List<string>(wCnt);
            for (int w = 0; w < wCnt && idx + 1 < parts.Length; w++)
            {
                lemmas.Add(parts[idx]);
                idx += 2;
            }

            if (idx >= parts.Length || !int.TryParse(parts[idx++], out int pCnt)) continue;
            var pointers = new List<WnPointer>(pCnt);
            for (int p = 0; p < pCnt && idx + 3 < parts.Length; p++)
            {
                string sym = parts[idx++];
                if (!long.TryParse(parts[idx++], out long tgtOffset)) { idx += 2; continue; }
                char tgtPos = parts[idx++][0];
                idx++;
                pointers.Add(new WnPointer(sym, tgtOffset, tgtPos));
            }

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
            string lemma = senseKey[..pct].Replace('_', ' ');
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
