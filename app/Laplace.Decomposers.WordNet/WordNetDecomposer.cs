using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.WordNet;

public sealed class WordNetDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.StrictSerial;

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    
    
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

    private const long EstimatedSynsets = 117_700L;

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WordNetDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private static readonly ConcurrentDictionary<string, byte> _vocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _vocabularyNames.Keys.ToArray();

    private static readonly string[] PosFiles = ["data.noun", "data.verb", "data.adj", "data.adv"];

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("WordNet_Synset");
        boot.AddType("WordNet_Sense");

        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_LEX_CATEGORY");
        boot.AddRelationType("HAS_DOMAIN_TOPIC");
        boot.AddRelationType("HAS_VERB_FRAME");
        boot.AddRelationType("IS_LEMMA_OF");
        boot.AddRelationType("HAS_SENSE");
        boot.AddRelationType("IS_SENSE_OF");

        foreach (var name in PointerTypes.Values)
            boot.AddRelationType(RelationTypeRegistry.Resolve(name).Canonical);

        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            _vocabularyNames.TryAdd(n, 0);
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
        using var fs = File.OpenRead(path);
        Span<byte> buf = stackalloc byte[65536];
        bool atLineStart = true;
        int read;
        while ((read = fs.Read(buf)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte c = buf[i];
                if (atLineStart)
                {
                    if (c >= (byte)'0' && c <= (byte)'9') n++;
                    atLineStart = false;
                }
                if (c == (byte)'\n') atLineStart = true;
            }
        }
        return n;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<SubstrateChange> StreamDataAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"wordnet/data-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;
        var frameTemplates = await LoadVerbFramesAsync(dictDir, ct);

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
        
        
        
        ConceptAnchor.EmitAnchor(b, syn.Offset, syn.SsType, Source);
        foreach (var lemma in syn.Lemmas)
            EmitSurface(b, lemma, Source);

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0) EmitSurface(b, def, Source);
        foreach (var ex in examples) EmitSurface(b, ex, Source);

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            EmitSurface(b, Lexnames[syn.LexFilenum], Source);

        foreach (var (frame, _) in syn.Frames)
            if (frame > 0 && frame < frameTemplates.Length && frameTemplates[frame] is { } tpl)
                EmitSurface(b, tpl, Source);

        // Close the entity pass under pointer references: every pointer target that
        // EmitSynsetAttestations will reference (via the same ConceptAnchor.SynsetId
        // resolve, line ~293) must be staged here in phase 1, or the attestation batch
        // fails referential integrity when a target synset is not independently emitted
        // (adjective-satellite pos skew / ili-map vs local-dict version drift). EmitAnchor
        // is content-addressed + idempotent, so this dedups against the target's own line.
        foreach (var ptr in syn.Pointers)
            if (PointerTypes.ContainsKey(ptr.Symbol))
                ConceptAnchor.EmitAnchor(b, ptr.TargetOffset, ptr.TargetPos, Source);
    }

    private static void EmitSynsetAttestations(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {
        
        
        Hash128? synAnchor = ConceptAnchor.SynsetId(syn.Offset, syn.SsType);
        if (synAnchor is null) return;
        Hash128 synId = synAnchor.Value;
        ConceptAnchor.AttestSynsetCategory(b, synId, Source, SourceTrust.StandardsDerived);

        foreach (var lemma in syn.Lemmas)
        {
            var lemmaId = RootSurface(lemma);
            if (lemmaId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "IS_SYNONYM_OF", synId, Source, SourceTrust.StandardsDerived));
            PosReference.Attest(b, lemmaId.Value, syn.SsType.ToString(),
                PosReference.PosTagset.WordNet, Source, null, SourceTrust.StandardsDerived,
                _vocabularyNames);
        }

        var (def, examples) = ParseGloss(syn.Gloss);
        if (def.Length > 0)
        {
            var defId = RootSurface(def);
            if (defId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_DEFINITION", defId.Value, Source, SourceTrust.StandardsDerived));
        }
        foreach (var ex in examples)
        {
            var exId = RootSurface(ex);
            if (exId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.StandardsDerived));
        }

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
        {
            string lexname = Lexnames[syn.LexFilenum];
            var lexId = RootSurface(lexname);
            if (lexId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_LEX_CATEGORY", lexId.Value,
                    Source, SourceTrust.StandardsDerived));
        }

        foreach (var (frame, wordNum) in syn.Frames)
        {
            if (frame <= 0 || frame >= frameTemplates.Length || frameTemplates[frame] is not { } tpl) continue;
            var tplId = RootSurface(tpl);
            if (tplId is null) continue;
            Hash128 subject = synId;
            if (wordNum > 0 && wordNum <= syn.Lemmas.Count)
            {
                var lemmaId = RootSurface(syn.Lemmas[wordNum - 1]);
                if (lemmaId is { } lid) subject = lid;
            }
            b.AddAttestation(NativeAttestation.Categorical(
                subject, "HAS_VERB_FRAME", tplId.Value, Source, SourceTrust.StandardsDerived));
        }

        foreach (var ptr in syn.Pointers)
        {
            if (!PointerTypes.TryGetValue(ptr.Symbol, out var typeName)) continue;
            
            
            Hash128? tgt = ConceptAnchor.SynsetId(ptr.TargetOffset, ptr.TargetPos);
            if (tgt is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                synId, typeName, tgt.Value, Source, SourceTrust.StandardsDerived));
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
                EmitSurface(b, s.SenseKey, Source);
                EmitSurface(b, s.Lemma, Source);
            }
            else
            {
                var senseId   = CategoryAnchor.Id(s.SenseKey);
                var lemmaId   = RootSurface(s.Lemma);
                var synAnchor = ConceptAnchor.SynsetId(s.Offset, s.Pos);  
                if (senseId is not null && lemmaId is not null && synAnchor is not null)
                {
                    CategoryAnchor.AttestCategory(b, senseId.Value, SenseTypeId, Source, SourceTrust.StandardsDerived);
                    b.AddAttestation(NativeAttestation.Categorical(
                        lemmaId.Value, "HAS_SENSE", senseId.Value, Source, SourceTrust.StandardsDerived,
                        magnitude: s.TagCount, arenaScale: 1.0));
                    b.AddAttestation(NativeAttestation.Categorical(
                        senseId.Value, "IS_SENSE_OF", synAnchor.Value, Source, SourceTrust.StandardsDerived));
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

    private static Hash128? EmitSurface(SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        var utf8 = System.Text.Encoding.UTF8.GetBytes(surface);
        if (surface.Contains('_'))
            return ContentWitnessBatch.TryAppendUnderscoredToBuilder(b, utf8, sourceId, out var id) ? id : null;
        return ContentWitnessBatch.TryAppendToBuilder(b, utf8, sourceId, out var root) ? root : null;
    }

    private static Hash128? RootSurface(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        string canonical = surface.Contains('_') ? Surface(surface) : surface;
        return ContentWitnessBatch.RootId(System.Text.Encoding.UTF8.GetBytes(canonical));
    }

    private static async Task<string?[]> LoadVerbFramesAsync(string dictDir, CancellationToken ct)
    {
        var templates = new string?[40];
        string path = Path.Combine(dictDir, "frames.vrb");
        if (!File.Exists(path)) return templates;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;
            if (!int.TryParse(System.Text.Encoding.UTF8.GetString(line[..sp]), out int num)) continue;
            if (num > 0 && num < templates.Length)
                templates[num] = System.Text.Encoding.UTF8.GetString(line[(sp + 1)..]).Trim();
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
                ReadOnlySpan<byte> line = lineMem.Span;
                int sp = line.IndexOf((byte)' ');
                if (sp <= 0) continue;
                var infId = EmitSurface(b, System.Text.Encoding.UTF8.GetString(line[..sp]), Source);
                if (infId is null) continue;
                int idx = sp + 1;
                while (idx < line.Length)
                {
                    int next = line[idx..].IndexOf((byte)' ');
                    ReadOnlySpan<byte> part = next < 0 ? line[idx..] : line.Slice(idx, next);
                    if (!part.IsEmpty)
                    {
                        var baseId = EmitSurface(b, System.Text.Encoding.UTF8.GetString(part), Source);
                        if (baseId is not null)
                            b.AddAttestation(NativeAttestation.Categorical(
                                baseId.Value, "IS_LEMMA_OF", infId.Value, Source, SourceTrust.StandardsDerived));
                    }
                    if (next < 0) break;
                    idx += next + 1;
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

            yield return new WnSynset(offset, ssType, lexFilenum, lemmas, pointers, gloss, frames);
        }
    }

    private static async IAsyncEnumerable<WnSense> ParseSensesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span;
            if (line.IsEmpty) continue;
            int sp = 0;
            while (sp < line.Length && line[sp] != (byte)' ') sp++;
            if (sp <= 0) continue;
            ReadOnlySpan<byte> senseKeySpan = line[..sp];
            string senseKey = System.Text.Encoding.UTF8.GetString(senseKeySpan);

            int idx = sp + 1;
            int offEnd = line[idx..].IndexOf((byte)' ');
            if (offEnd < 0) continue;
            if (!long.TryParse(System.Text.Encoding.UTF8.GetString(line.Slice(idx, offEnd)), out long offset)) continue;
            idx += offEnd + 1;
            int tagStart = line[idx..].IndexOf((byte)' ');
            if (tagStart < 0) continue;
            idx += tagStart + 1;
            if (!int.TryParse(System.Text.Encoding.UTF8.GetString(line[idx..]), out int tagCount)) tagCount = 0;

            int pct = senseKey.IndexOf('%');
            if (pct <= 0 || pct + 1 >= senseKey.Length) continue;
            string lemma = senseKey[..pct].Replace('_', ' ');
            char pos = senseKey[pct + 1] switch
            {
                '1' => 'n', '2' => 'v', '3' => 'a', '4' => 'r', '5' => 's', _ => 'n',
            };

            string? normKey = SourceEntityIdConventions.NormalizeSenseKey(senseKey);
            if (normKey is null) continue;
            yield return new WnSense(normKey, offset, pos, lemma, tagCount);
        }
    }

    private sealed record WnSynset(
        long Offset, char SsType, int LexFilenum,
        List<string> Lemmas, List<WnPointer> Pointers, string Gloss,
        List<(int Frame, int WordNum)> Frames);

    private readonly record struct WnPointer(string Symbol, long TargetOffset, char TargetPos);

    private sealed record WnSense(string SenseKey, long Offset, char Pos, string Lemma, int TagCount);
}
