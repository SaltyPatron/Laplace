using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.WordNet;

public sealed class WordNetDecomposer : DecomposerMultiPhase<WordNetSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = WordNetSource.SourceId;
    public static readonly Hash128 TrustClass = WordNetSource.TrustClass;

    private static Dictionary<string, string> PointerTypes => WordNetSource.PointerTypes;

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

    public override int LayerOrder => 2;

    private static readonly ConcurrentDictionary<string, byte> _vocabularyNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _vocabularyNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _vocabularyNames;

    private static readonly string[] PosFiles = ["data.noun", "data.verb", "data.adj", "data.adv"];

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.WarnIfCiliMapMissing(context.Logger, SourceName);

        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        int batch = IngestSizing.ResolveForSource(
            IngestSourceProfile.WordNet,
            options.BatchSize > 1 ? options.BatchSize : null).RecordBatchSize;
        var frames = await LoadVerbFramesAsync(dictDir, ct);

        await foreach (var c in RunPhaseAsync(new DataPhase(frames, batch), context, options, ct))
            yield return c;

        if (options.MaxInputUnits > 0) yield break;

        var uncapped = options with { MaxInputUnits = 0 };

        await foreach (var c in RunPhaseAsync(new SensePhase(batch), context, uncapped, ct))
            yield return c;

        await foreach (var c in RunPhaseAsync(new ExcPhase(batch), context, uncapped, ct))
            yield return c;

        await foreach (var c in RunPhaseAsync(new SentsPhase(batch), context, uncapped, ct))
            yield return c;
    }

    private abstract class WnComposePhase<T> : ComposeDecomposerPhase<T>
    {
        private readonly int _batch;

        protected WnComposePhase(int batch) => _batch = batch;

        public override Hash128 SourceId => Source;
        public override string SourceName => "WordNetDecomposer";
        public override int LayerOrder => 2;
        public override Hash128 TrustClassId => TrustClass;
        protected override double SourceTrust => TC.StandardsDerived;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId, BatchLabelPrefix, _batch, options, context.Reader,
                    IngestSourceProfile.WordNet),
                options);
    }

    private sealed class DataPhase : WnComposePhase<WnSynset>
    {
        private readonly string?[] _frames;

        public DataPhase(string?[] frames, int batch) : base(batch) => _frames = frames;

        protected override string PhaseLabel => "data";

        protected override void Compose(WnSynset syn, SubstrateChangeBuilder b)
        {
            EmitSynsetEntities(b, syn, _frames);
            EmitSynsetAttestations(b, syn, _frames);
        }

        protected override async IAsyncEnumerable<WnSynset> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string dictDir = Path.Combine(ecosystemPath, "WordNet-3.0", "dict");
            await foreach (var syn in ParseAllSynsetsAsync(dictDir, ct))
                yield return syn;
        }
    }

    private sealed class SensePhase : WnComposePhase<WnSense>
    {
        public SensePhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "sense";
        protected override void Compose(WnSense s, SubstrateChangeBuilder b) => ComposeSense(s, b);
        protected override async IAsyncEnumerable<WnSense> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string path = Path.Combine(ecosystemPath, "WordNet-3.0", "dict", "index.sense");
            await foreach (var s in ParseSensesAsync(path, ct))
                yield return s;
        }
    }

    private sealed class ExcPhase : WnComposePhase<WnExcLine>
    {
        public ExcPhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "exc";
        protected override void Compose(WnExcLine exc, SubstrateChangeBuilder b) => ComposeExcLine(exc, b);
        protected override async IAsyncEnumerable<WnExcLine> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string dictDir = Path.Combine(ecosystemPath, "WordNet-3.0", "dict");
            await foreach (var exc in ParseExceptionsAsync(dictDir, ct))
                yield return exc;
        }
    }

    private sealed class SentsPhase : WnComposePhase<WnVerbSentEntry>
    {
        public SentsPhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "sents";
        protected override void Compose(WnVerbSentEntry entry, SubstrateChangeBuilder b) =>
            ComposeVerbSentEntry(entry, b);
        protected override async IAsyncEnumerable<WnVerbSentEntry> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string dictDir = Path.Combine(ecosystemPath, "WordNet-3.0", "dict");
            await foreach (var entry in ParseVerbSentencesAsync(dictDir, ct))
                yield return entry;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        var paths = PosFiles
            .Select(pos => Path.Combine(dictDir, pos))
            .Where(File.Exists)
            .ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("synsets", paths, options.MaxInputUnits, ct));
        var files = paths.Select(p => new IngestFileSpec(Path.GetFileName(p), p, CountSynsetLines(p))).ToList();
        // Decompose runs four streams (synsets, index.sense, *.exc,
        // sentidx.vrb) against one consumed-units counter — a synsets-only
        // total made progress read ~284%. Uncapped inventory counts them all;
        // the capped path stays synsets-only because DecomposeAsync skips the
        // other streams entirely when MaxInputUnits > 0.
        foreach (var extra in new[] { "index.sense", "noun.exc", "verb.exc", "adj.exc", "adv.exc", "sentidx.vrb" })
        {
            string ep = Path.Combine(dictDir, extra);
            if (File.Exists(ep)) files.Add(new IngestFileSpec(extra, ep, CountNonEmptyLines(ep)));
        }
        long total = files.Sum(f => f.InputUnits);
        return Task.FromResult<IngestInventory?>(new IngestInventory("records", total, files));
    }

    private static long CountNonEmptyLines(string path)
    {
        long n = 0;
        using var fs = File.OpenRead(path);
        Span<byte> buf = stackalloc byte[65536];
        bool lineHasContent = false;
        int read;
        while ((read = fs.Read(buf)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\n')
                {
                    if (lineHasContent) n++;
                    lineHasContent = false;
                }
                else if (c != (byte)'\r' && c != (byte)' ') lineHasContent = true;
            }
        }
        if (lineHasContent) n++;
        return n;
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
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

    private static async IAsyncEnumerable<WnSynset> ParseAllSynsetsAsync(
        string dictDir, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var posFile in PosFiles)
        {
            string filePath = Path.Combine(dictDir, posFile);
            if (!File.Exists(filePath)) continue;
            await foreach (var syn in ParseDataAsync(filePath, ct))
                yield return syn;
        }
    }

    private static void EmitSynsetEntities(SubstrateChangeBuilder b, WnSynset syn, string?[] frameTemplates)
    {



        ConceptAnchor.EmitAnchor(b, syn.Offset, syn.SsType, Source);
        foreach (var lemma in syn.Lemmas)
            EmitSurface(b, lemma, Source);

        var (defs, examples) = ParseGloss(syn.Gloss);
        foreach (var d in defs) EmitSurface(b, d, Source);
        foreach (var ex in examples) EmitSurface(b, ex, Source);

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            EmitSurface(b, Lexnames[syn.LexFilenum], Source);

        foreach (var (frame, _) in syn.Frames)
            if (frame > 0 && frame < frameTemplates.Length && frameTemplates[frame] is { } tpl)
                EmitSurface(b, tpl, Source);
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

        var (defs, examples) = ParseGloss(syn.Gloss);
        foreach (var d in defs)
        {
            var defId = RootSurface(d);
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


            if (syn.SsType == 'v' && ptr.Symbol == "@")
                typeName = "MANNER_OF";

            Hash128? tgt = ConceptAnchor.SynsetId(ptr.TargetOffset, ptr.TargetPos);
            if (tgt is null) continue;


            Hash128 subject = synId;
            if (ptr.SrcWord > 0 && ptr.SrcWord <= syn.Lemmas.Count)
            {
                var srcId = RootSurface(syn.Lemmas[ptr.SrcWord - 1]);
                if (srcId is { } sid) subject = sid;
            }
            b.AddAttestation(NativeAttestation.Categorical(
                subject, typeName, tgt.Value, Source, SourceTrust.StandardsDerived));
        }
    }

    private static void ComposeSense(WnSense s, SubstrateChangeBuilder b)
    {
        EmitSurface(b, s.SenseKey, Source);
        EmitSurface(b, s.Lemma, Source);

        var senseId = SenseAnchor.IdNormalized(s.SenseKey);
        var lemmaId = RootSurface(s.Lemma);
        var synAnchor = ConceptAnchor.SynsetId(s.Offset, s.Pos);
        if (senseId is null || lemmaId is null || synAnchor is null) return;

        SenseAnchor.AttestSenseCategory(b, senseId.Value, Source, SourceTrust.StandardsDerived);
        b.AddAttestation(NativeAttestation.Categorical(
            lemmaId.Value, "HAS_SENSE", senseId.Value, Source, SourceTrust.StandardsDerived,
            magnitude: s.TagCount, arenaScale: 1.0));
        b.AddAttestation(NativeAttestation.Categorical(
            senseId.Value, "IS_SENSE_OF", synAnchor.Value, Source, SourceTrust.StandardsDerived));
        b.AddAttestation(NativeAttestation.Categorical(
            senseId.Value, "HAS_NAME_ALIAS", lemmaId.Value, Source, SourceTrust.StandardsDerived));
        PosReference.Attest(b, senseId.Value, s.Pos.ToString(),
            PosReference.PosTagset.WordNet, Source, null, SourceTrust.StandardsDerived,
            _vocabularyNames);
        int lexFilenum = ParseLexFilenum(s.SenseKey);
        if (lexFilenum >= 0 && lexFilenum < Lexnames.Length)
        {
            EmitSurface(b, Lexnames[lexFilenum], Source);
            var lexId = RootSurface(Lexnames[lexFilenum]);
            if (lexId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    senseId.Value, "HAS_LEX_CATEGORY", lexId.Value, Source, SourceTrust.StandardsDerived));
        }
    }

    private static string Surface(string lemma) => lemma.Replace('_', ' ');


    private static int ParseLexFilenum(string senseKey)
    {
        int pct = senseKey.IndexOf('%');
        if (pct < 0 || pct + 1 >= senseKey.Length) return -1;
        var fields = senseKey[(pct + 1)..].Split(':');
        return fields.Length >= 2 && int.TryParse(fields[1], out var n) ? n : -1;
    }

    private static Hash128? EmitSurface(SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        var utf8 = System.Text.Encoding.UTF8.GetBytes(surface);
        if (surface.Contains('_'))
            return ContentTierSpine.TryStageUnderscoredIntoBuilder(b, utf8, sourceId, out var id) ? id : null;
        return ContentTierSpine.TryStageIntoBuilder(b, utf8, sourceId, out var root) ? root : null;
    }

    private static Hash128? RootSurface(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        string canonical = surface.Contains('_') ? Surface(surface) : surface;
        return ContentTierSpine.ResolveRoot(System.Text.Encoding.UTF8.GetBytes(canonical));
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

    private readonly record struct WnExcLine(string Inflected, List<string> Bases);

    private static async IAsyncEnumerable<WnExcLine> ParseExceptionsAsync(
        string dictDir, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var excFile in new[] { "noun.exc", "verb.exc", "adj.exc", "adv.exc" })
        {
            string path = Path.Combine(dictDir, excFile);
            if (!File.Exists(path)) continue;
            await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
            {
                ReadOnlySpan<byte> line = lineMem.Span;
                int sp = line.IndexOf((byte)' ');
                if (sp <= 0) continue;
                string inf = System.Text.Encoding.UTF8.GetString(line[..sp]);
                if (inf.Length == 0) continue;
                var bases = new List<string>();
                int idx = sp + 1;
                while (idx < line.Length)
                {
                    int next = line[idx..].IndexOf((byte)' ');
                    ReadOnlySpan<byte> part = next < 0 ? line[idx..] : line.Slice(idx, next);
                    if (!part.IsEmpty)
                        bases.Add(System.Text.Encoding.UTF8.GetString(part));
                    if (next < 0) break;
                    idx += next + 1;
                }
                if (bases.Count > 0) yield return new WnExcLine(inf, bases);
            }
        }
    }

    private static void ComposeExcLine(WnExcLine exc, SubstrateChangeBuilder b)
    {
        var infId = EmitSurface(b, exc.Inflected, Source);
        if (infId is null) return;
        foreach (var baseStr in exc.Bases)
        {
            var baseId = EmitSurface(b, baseStr, Source);
            if (baseId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    baseId.Value, "IS_LEMMA_OF", infId.Value, Source, SourceTrust.StandardsDerived));
        }
    }

    private readonly record struct WnVerbSentEntry(Hash128 SynId, List<string> SentTexts);

    private static async IAsyncEnumerable<WnVerbSentEntry> ParseVerbSentencesAsync(
        string dictDir, [EnumeratorCancellation] CancellationToken ct)
    {
        string idxPath = Path.Combine(dictDir, "sentidx.vrb");
        string sentsPath = Path.Combine(dictDir, "sents.vrb");
        if (!File.Exists(idxPath) || !File.Exists(sentsPath)) yield break;

        var sentences = await LoadVerbSentencesAsync(sentsPath, ct);
        if (sentences.Count == 0) yield break;
        var senseIndex = await LoadSenseKeyIndexAsync(Path.Combine(dictDir, "index.sense"), ct);
        if (senseIndex.Count == 0) yield break;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(idxPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            if (line.IsEmpty) continue;
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;

            string senseKey = System.Text.Encoding.UTF8.GetString(line[..sp]);
            string? normKey = SourceEntityIdConventions.NormalizeSenseKey(senseKey);
            if (normKey is null || !senseIndex.TryGetValue(normKey, out var syn)) continue;

            Hash128? synId = ConceptAnchor.SynsetId(syn.Offset, syn.Pos);
            if (synId is null) continue;

            ReadOnlySpan<byte> idList = line[(sp + 1)..];
            var texts = new List<string>();
            int idStart = 0;
            for (int i = 0; i <= idList.Length; i++)
            {
                if (i < idList.Length && idList[i] != (byte)',') continue;
                var idSpan = idList[idStart..i].Trim((byte)' ');
                idStart = i + 1;
                if (idSpan.IsEmpty) continue;
                if (!int.TryParse(System.Text.Encoding.UTF8.GetString(idSpan), out int sentId)) continue;
                if (sentences.TryGetValue(sentId, out string? text) && text.Length > 0)
                    texts.Add(text);
            }
            if (texts.Count > 0) yield return new WnVerbSentEntry(synId.Value, texts);
        }
    }

    private static void ComposeVerbSentEntry(WnVerbSentEntry entry, SubstrateChangeBuilder b)
    {
        foreach (var text in entry.SentTexts)
        {
            var exId = EmitSurface(b, text, Source);
            if (exId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    entry.SynId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.StandardsDerived));
        }
    }

    private static async Task<Dictionary<int, string>> LoadVerbSentencesAsync(
        string path, CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            if (line.IsEmpty) continue;
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;
            if (!int.TryParse(System.Text.Encoding.UTF8.GetString(line[..sp]), out int id)) continue;
            string text = System.Text.Encoding.UTF8.GetString(line[(sp + 1)..]).Trim();
            if (text.Length > 0) map[id] = text;
        }
        return map;
    }

    private static async Task<Dictionary<string, (long Offset, char Pos)>> LoadSenseKeyIndexAsync(
        string path, CancellationToken ct)
    {
        var map = new Dictionary<string, (long, char)>(StringComparer.Ordinal);
        await foreach (var s in ParseSensesAsync(path, ct))
            map.TryAdd(s.SenseKey, (s.Offset, s.Pos));
        return map;
    }

    private static (List<string> Defs, List<string> Examples) ParseGloss(string gloss)
    {
        var examples = new List<string>();
        if (string.IsNullOrEmpty(gloss)) return (new List<string>(), examples);
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



        return (DelimitedContent.Split(def.ToString(), ';'), examples);
    }

    private static async IAsyncEnumerable<WnSynset> ParseDataAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            string line = System.Text.Encoding.UTF8.GetString(lineMem.Span);
            if (TryParseDataLine(line, out var syn))
                yield return syn;
        }
    }

    internal static bool TryParseDataLine(string line, out WnSynset syn)
    {
        syn = null!;
        if (line.Length == 0 || line[0] == ' ') return false;

        int glossSep = line.IndexOf(" | ", StringComparison.Ordinal);
        string synData = glossSep >= 0 ? line[..glossSep] : line;
        string gloss = glossSep >= 0 ? line[(glossSep + 3)..] : "";

        var parts = synData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;

        if (!long.TryParse(parts[0], out long offset)) return false;
        if (!int.TryParse(parts[1], out int lexFilenum)) lexFilenum = -1;
        char ssType = parts[2].Length > 0 ? parts[2][0] : 'n';
        if (!int.TryParse(parts[3], NumberStyles.HexNumber, null, out int wCnt)) return false;

        int idx = 4;
        var lemmas = new List<string>(wCnt);
        for (int w = 0; w < wCnt && idx + 1 < parts.Length; w++)
        {
            lemmas.Add(parts[idx]);
            idx += 2;
        }

        if (idx >= parts.Length || !int.TryParse(parts[idx++], out int pCnt)) return false;
        var pointers = new List<WnPointer>(pCnt);
        for (int p = 0; p < pCnt && idx + 3 < parts.Length; p++)
        {
            string sym = parts[idx++];
            if (!long.TryParse(parts[idx++], out long tgtOffset)) { idx += 2; continue; }
            char tgtPos = parts[idx++][0];



            string srcTgt = parts[idx++];
            int srcWord = srcTgt.Length >= 4 && int.TryParse(srcTgt.AsSpan(0, 2), NumberStyles.HexNumber, null, out int sw) ? sw : 0;
            int tgtWord = srcTgt.Length >= 4 && int.TryParse(srcTgt.AsSpan(2, 2), NumberStyles.HexNumber, null, out int tw) ? tw : 0;
            pointers.Add(new WnPointer(sym, tgtOffset, tgtPos, srcWord, tgtWord));
        }

        var frames = new List<(int Frame, int WordNum)>();
        if (ssType == 'v' && idx < parts.Length && int.TryParse(parts[idx], out int fCnt) && fCnt > 0)
        {
            idx++;
            for (int f = 0; f < fCnt; f++)
            {
                if (idx + 2 >= parts.Length) break;
                if (parts[idx] != "+") break;
                idx++;
                if (!int.TryParse(parts[idx++], out int fNum)) break;
                if (!int.TryParse(parts[idx++], NumberStyles.HexNumber, null, out int wNum)) break;
                frames.Add((fNum, wNum));
            }
        }

        syn = new WnSynset(offset, ssType, lexFilenum, lemmas, pointers, gloss, frames);
        return true;
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
                '1' => 'n',
                '2' => 'v',
                '3' => 'a',
                '4' => 'r',
                '5' => 's',
                _ => 'n',
            };

            string? normKey = SourceEntityIdConventions.NormalizeSenseKey(senseKey);
            if (normKey is null) continue;
            yield return new WnSense(normKey, offset, pos, lemma, tagCount);
        }
    }

    internal sealed record WnSynset(
        long Offset, char SsType, int LexFilenum,
        List<string> Lemmas, List<WnPointer> Pointers, string Gloss,
        List<(int Frame, int WordNum)> Frames);

    internal readonly record struct WnPointer(string Symbol, long TargetOffset, char TargetPos, int SrcWord, int TgtWord);

    private sealed record WnSense(string SenseKey, long Offset, char Pos, string Lemma, int TagCount);
}
