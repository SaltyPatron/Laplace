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

    /// <summary>
    /// The language this source asserts for its lexical surfaces, from the manifest.
    /// Read here rather than written here so the fact has ONE home: a literal "eng" in a
    /// decomposer is the per-source hand-roll that left nine English sources emitting no
    /// language at all. Null when the manifest declares no scope.
    /// </summary>
    private static readonly Hash128? LanguageScopeId =
        EtlManifest.TryGet("wordnet", out var _wnRow) ? _wnRow.LanguageScopeId : null;
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
    private static readonly string[] ExcFiles = ["noun.exc", "verb.exc", "adj.exc", "adv.exc"];
    private static readonly string[] PhysicalFiles =
    [
        "frames.vrb",
        "data.noun", "data.verb", "data.adj", "data.adv",
        "index.sense",
        "noun.exc", "verb.exc", "adj.exc", "adv.exc",
        "sents.vrb", "sentidx.vrb",
    ];

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // GH #520: same hard-fail as OMW/SemLink/MapNet — a missing CILI map
        // silently drops synset anchors and leaves WordNet unmeshed.
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        int batch = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.WordNet, options);

        string framesPath = Path.Combine(dictDir, "frames.vrb");
        if (File.Exists(framesPath))
        {
            await foreach (var c in RunPhaseAsync(
                new FramePhase(batch), context, options, "frames.vrb", framesPath, ct))
                yield return c;
        }

        foreach (string posFile in PosFiles)
        {
            string path = Path.Combine(dictDir, posFile);
            if (!File.Exists(path)) continue;
            await foreach (var c in RunPhaseAsync(
                new DataPhase(posFile, batch), context, options, posFile, path, ct))
                yield return c;
        }

        string sensePath = Path.Combine(dictDir, "index.sense");
        if (File.Exists(sensePath))
        {
            await foreach (var c in RunPhaseAsync(
                new SensePhase(batch), context, options, "index.sense", sensePath, ct))
                yield return c;
        }

        foreach (string excFile in ExcFiles)
        {
            string path = Path.Combine(dictDir, excFile);
            if (!File.Exists(path)) continue;
            await foreach (var c in RunPhaseAsync(
                new ExcPhase(excFile, batch), context, options, excFile, path, ct))
                yield return c;
        }

        string sentsPath = Path.Combine(dictDir, "sents.vrb");
        if (File.Exists(sentsPath))
        {
            await foreach (var c in RunPhaseAsync(
                new SentenceTextPhase(batch), context, options, "sents.vrb", sentsPath, ct))
                yield return c;
        }

        string sentIdxPath = Path.Combine(dictDir, "sentidx.vrb");
        if (File.Exists(sentIdxPath))
        {
            await foreach (var c in RunPhaseAsync(
                new SentenceIndexPhase(batch), context, options, "sentidx.vrb", sentIdxPath, ct))
                yield return c;
        }
    }

    private abstract class WnComposePhase<T> : ComposeDecomposerPhase<T>
    {
        protected WnComposePhase(int batch) { }

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
                    SourceId, BatchLabelPrefix, options, context.Reader,
                    IngestSourceProfile.WordNet),
                options);
    }

    private sealed class FramePhase : WnComposePhase<WnVerbFrame>
    {
        public FramePhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "frames.vrb";

        protected override void Compose(WnVerbFrame frame, SubstrateChangeBuilder b)
        {
            Hash128? frameId = ReferenceAnchor.Emit(
                b, ReferenceIdentityKind.WordNetVerbFrame,
                frame.Number.ToString(CultureInfo.InvariantCulture),
                EntityTypeRegistry.SourceReference, Source, SourceTrust.StandardsDerived);
            Hash128? templateId = EmitSurface(b, frame.Template, Source);
            if (frameId is { } fid && templateId is { } tid)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    fid, WordNetSource.CorrespondsToTypeId, tid, Source,
                    null, SourceTrust.StandardsDerived));
        }

        protected override IAsyncEnumerable<WnVerbFrame> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseVerbFramesAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", "frames.vrb"), ct);
    }

    private sealed class DataPhase : WnComposePhase<WnSynset>
    {
        private readonly string _fileName;

        public DataPhase(string fileName, int batch) : base(batch) => _fileName = fileName;

        protected override string PhaseLabel => _fileName;

        protected override void Compose(WnSynset syn, SubstrateChangeBuilder b)
        {
            EmitSynsetEntities(b, syn);
            EmitSynsetAttestations(b, syn);
        }

        protected override IAsyncEnumerable<WnSynset> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseDataAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", _fileName), ct);
    }

    private sealed class SensePhase : WnComposePhase<WnSense>
    {
        public SensePhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "index.sense";
        protected override void Compose(WnSense s, SubstrateChangeBuilder b) => ComposeSense(s, b);
        protected override IAsyncEnumerable<WnSense> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseSensesAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", "index.sense"), ct);
    }

    private sealed class ExcPhase : WnComposePhase<WnExcLine>
    {
        private readonly string _fileName;
        public ExcPhase(string fileName, int batch) : base(batch) => _fileName = fileName;
        protected override string PhaseLabel => _fileName;
        protected override void Compose(WnExcLine exc, SubstrateChangeBuilder b) => ComposeExcLine(exc, b);
        protected override IAsyncEnumerable<WnExcLine> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseExceptionFileAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", _fileName), ct);
    }

    private sealed class SentenceTextPhase : WnComposePhase<WnVerbSentence>
    {
        public SentenceTextPhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "sents.vrb";

        protected override void Compose(WnVerbSentence sentence, SubstrateChangeBuilder b)
        {
            Hash128? sentenceId = ReferenceAnchor.Emit(
                b, ReferenceIdentityKind.WordNetVerbSentence,
                sentence.Number.ToString(CultureInfo.InvariantCulture),
                EntityTypeRegistry.SourceReference, Source, SourceTrust.StandardsDerived);
            Hash128? textId = EmitSurface(b, sentence.Text, Source);
            if (sentenceId is { } sid && textId is { } tid)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    sid, WordNetSource.CorrespondsToTypeId, tid, Source,
                    null, SourceTrust.StandardsDerived));
        }

        protected override IAsyncEnumerable<WnVerbSentence> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseVerbSentenceTextsAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", "sents.vrb"), ct);
    }

    private sealed class SentenceIndexPhase : WnComposePhase<WnVerbSentEntry>
    {
        public SentenceIndexPhase(int batch) : base(batch) { }
        protected override string PhaseLabel => "sentidx.vrb";
        protected override void Compose(WnVerbSentEntry entry, SubstrateChangeBuilder b) =>
            ComposeVerbSentEntry(entry, b);
        protected override IAsyncEnumerable<WnVerbSentEntry> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            ParseVerbSentenceIndexAsync(Path.Combine(ecosystemPath, "WordNet-3.0", "dict", "sentidx.vrb"), ct);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        List<IngestFileSpec> files;
        if (context.HasArtifactGraph)
        {
            files = context.SelectedArtifacts
                .Select(artifact => new IngestFileSpec(
                    artifact.FileLabel, artifact.Path,
                    EtlInventory.EstimateNewlineCount(artifact.Path, ct)))
                .ToList();
        }
        else
        {
            string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
            files = PhysicalFiles
                .Select(name => (Name: name, Path: Path.Combine(dictDir, name)))
                .Where(static file => File.Exists(file.Path))
                .Select(file => new IngestFileSpec(
                    file.Name, file.Path, EtlInventory.EstimateNewlineCount(file.Path, ct)))
                .ToList();
        }

        if (files.Count == 0) return Task.FromResult<IngestInventory?>(null);
        long total = files.Sum(static file => file.InputUnits);
        long effective = options.MaxInputUnits > 0 ? Math.Min(total, options.MaxInputUnits) : total;
        return Task.FromResult<IngestInventory?>(new IngestInventory("records", effective, files));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits ?? EstimatedSynsets;
    }

    private static void EmitSynsetEntities(SubstrateChangeBuilder b, WnSynset syn)
    {
        ConceptAnchor.EmitAnchor(b, syn.Offset, syn.SsType, Source);
        foreach (var lemma in syn.Lemmas)
            EmitSurface(b, lemma, Source);

        var (defs, examples) = ParseGloss(syn.Gloss);
        foreach (var d in defs) EmitSurface(b, d, Source);
        foreach (var ex in examples) EmitSurface(b, ex, Source);

        if (syn.LexFilenum >= 0 && syn.LexFilenum < Lexnames.Length)
            EmitSurface(b, Lexnames[syn.LexFilenum], Source);
    }

    private static void EmitSynsetAttestations(SubstrateChangeBuilder b, WnSynset syn)
    {
        Hash128? synAnchor = ConceptAnchor.SynsetId(syn.Offset, syn.SsType);
        if (synAnchor is null) return;
        Hash128 synId = synAnchor.Value;
        ConceptAnchor.AttestSynsetCategory(b, synId, Source, SourceTrust.StandardsDerived);

        foreach (var lemma in syn.Lemmas)
        {
            var lemmaId = RootSurface(lemma);
            if (lemmaId is null) continue;
            // Synset membership is carried by index.sense at its exact lexical grain:
            // lemma -> HAS_SENSE -> sense -> IS_SENSE_OF -> synset. Repeating that same
            // source observation here as lemma -> IS_SYNONYM_OF -> synset made one WordNet
            // fact look like two independent witnesses and discarded the sense identity.
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
            if (frame <= 0) continue;
            Hash128? frameId = ReferenceAnchor.Id(
                ReferenceIdentityKind.WordNetVerbFrame,
                frame.ToString(CultureInfo.InvariantCulture));
            if (frameId is null) continue;
            Hash128 subject = synId;
            if (wordNum > 0 && wordNum <= syn.Lemmas.Count)
            {
                var lemmaId = RootSurface(syn.Lemmas[wordNum - 1]);
                if (lemmaId is { } lid) subject = lid;
            }
            b.AddAttestation(NativeAttestation.Categorical(
                subject, "HAS_VERB_FRAME", frameId.Value, Source, SourceTrust.StandardsDerived));
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
        EmitSurface(b, s.Lemma, Source);

        var senseId = SenseAnchor.EmitExact(
            b, s.SenseKey, Source, SourceTrust.StandardsDerived);
        var compatibilityId = SenseAnchor.Emit(
            b, s.SenseKey, Source, SourceTrust.StandardsDerived);
        if (senseId is null) return;

        // Older bridges frequently retain only lemma%ss_type:lex_filenum:lex_id. That
        // serialization is not unique for adjective satellites, so keep it as an explicit
        // compatibility hub and connect it to every exact source sense instead of using it
        // as identity. The two real PWN senses remain distinct on HAS_SENSE/IS_SENSE_OF.
        if (compatibilityId is { } alias && alias != senseId.Value)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                alias, WordNetSource.CorrespondsToTypeId, senseId.Value, Source,
                null, SourceTrust.StandardsDerived));

        var lemmaId = RootSurface(s.Lemma);
        var synAnchor = ConceptAnchor.SynsetId(s.Offset, s.Pos);
        if (lemmaId is null || synAnchor is null) return;

        // The source's declared language scope, recorded rather than inferred. Emitted on
        // the LEMMA and the SENSE and deliberately not on the synset: a synset is
        // ILI-shared across every wordnet, so it is language-neutral and this source does
        // not assert otherwise. lexical.senses() returns sense_id, and that is the id the elector
        // compares against the prompt's language, so the sense edge is the load-bearing one.
        // Null context, matching OMW: the object IS the language, so a language context
        // would be circular.
        if (LanguageScopeId is { } langId)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, EtlSource.LanguageScopeRelation, langId, Source,
                SourceTrust.StandardsDerived));
            b.AddAttestation(NativeAttestation.Categorical(
                senseId.Value, EtlSource.LanguageScopeRelation, langId, Source,
                SourceTrust.StandardsDerived));
        }
        b.AddAttestation(NativeAttestation.Categorical(
            lemmaId.Value, "HAS_SENSE", senseId.Value, Source, SourceTrust.StandardsDerived,
            magnitude: s.WitnessedMagnitude, arenaScale: 1.0));
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

    private readonly record struct WnVerbFrame(int Number, string Template);

    private static async IAsyncEnumerable<WnVerbFrame> ParseVerbFramesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path)) yield break;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;
            if (!int.TryParse(System.Text.Encoding.UTF8.GetString(line[..sp]), out int num)) continue;
            string template = System.Text.Encoding.UTF8.GetString(line[(sp + 1)..]).Trim();
            if (num > 0 && template.Length > 0)
                yield return new WnVerbFrame(num, template);
        }
    }

    private readonly record struct WnExcLine(string Inflected, List<string> Bases);

    private static async IAsyncEnumerable<WnExcLine> ParseExceptionFileAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path)) yield break;
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

    private readonly record struct WnVerbSentence(int Number, string Text);
    private readonly record struct WnVerbSentEntry(string SenseKey, List<int> SentenceNumbers);

    private static async IAsyncEnumerable<WnVerbSentence> ParseVerbSentenceTextsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path)) yield break;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            if (line.IsEmpty) continue;
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;
            if (!int.TryParse(System.Text.Encoding.UTF8.GetString(line[..sp]), out int id)) continue;
            string text = System.Text.Encoding.UTF8.GetString(line[(sp + 1)..]).Trim();
            if (id > 0 && text.Length > 0)
                yield return new WnVerbSentence(id, text);
        }
    }

    private static async IAsyncEnumerable<WnVerbSentEntry> ParseVerbSentenceIndexAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path)) yield break;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> line = lineMem.Span.Trim((byte)' ');
            if (line.IsEmpty) continue;
            int sp = line.IndexOf((byte)' ');
            if (sp <= 0) continue;

            string rawSenseKey = System.Text.Encoding.UTF8.GetString(line[..sp]);
            string? exactKey = SourceEntityIdConventions.NormalizeExactSenseKey(rawSenseKey);
            if (exactKey is null) continue;

            ReadOnlySpan<byte> idList = line[(sp + 1)..];
            var sentenceNumbers = new List<int>();
            int idStart = 0;
            for (int i = 0; i <= idList.Length; i++)
            {
                if (i < idList.Length && idList[i] != (byte)',') continue;
                var idSpan = idList[idStart..i].Trim((byte)' ');
                idStart = i + 1;
                if (idSpan.IsEmpty) continue;
                if (int.TryParse(System.Text.Encoding.UTF8.GetString(idSpan), out int sentId) && sentId > 0)
                    sentenceNumbers.Add(sentId);
            }
            if (sentenceNumbers.Count > 0)
                yield return new WnVerbSentEntry(exactKey, sentenceNumbers);
        }
    }

    private static void ComposeVerbSentEntry(WnVerbSentEntry entry, SubstrateChangeBuilder b)
    {
        Hash128? senseId = SenseAnchor.EmitExact(
            b, entry.SenseKey, Source, SourceTrust.StandardsDerived);
        if (senseId is null) return;
        foreach (int sentenceNumber in entry.SentenceNumbers)
        {
            Hash128? sentenceId = ReferenceAnchor.Declare(
                b, ReferenceIdentityKind.WordNetVerbSentence,
                sentenceNumber.ToString(CultureInfo.InvariantCulture),
                EntityTypeRegistry.SourceReference, Source);
            if (sentenceId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    senseId.Value, "HAS_EXAMPLE", sentenceId.Value,
                    Source, SourceTrust.StandardsDerived));
        }
    }

    // internal, not private: the gloss/example split is the thing that shredded one
    // WordNet definition into several facts, so it needs a test that can call it.
    internal static (List<string> Defs, List<string> Examples) ParseGloss(string gloss)
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

        // ONE SYNSET, ONE GLOSS. The quote walk above already lifted every example out of
        // `def`; what remains is the definition, and it is a single string. Splitting it on
        // ';' was reading a separator that is not there: in WordNet's data files ';'
        // separates the definition from the QUOTED examples, and those are gone by now — so
        // the only semicolons left are INSIDE the definition.
        //
        // MEASURED 2026-08-16 on synset f59f0970 (cat). Gloss:
        //   "feline mammal usually having thick soft fur and no ability to roar:
        //    domestic cats; wildcats"
        // deposited as THREE HAS_DEFINITION facts — the whole string (via OMW/CILI, which
        // do not split), plus "...domestic cats" and "wildcats" from this line. Consensus
        // then divides across them: the real gloss and the fragment "wildcats" both landed
        // at eff_mu 1319.9, so which one a read returns as the top definition was a
        // tie-break, not a rating. A fragment is not a competing claim about meaning; it is
        // the same claim cut in half, and it cannot lose to the whole.
        // Removing the quoted examples leaves their separators behind — "definition; ; " —
        // so trim ';' and whitespace together until stable rather than in one pass.
        string definition = def.ToString().TrimEnd(' ', '\t', ';').TrimStart();
        return (definition.Length == 0 ? new List<string>() : new List<string> { definition },
                examples);
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
            // index.sense is `sense_key synset_offset sense_number tag_cnt`. sense_number
            // is WordNet's own frequency ordering within the lemma (1 = most common) and
            // was being STEPPED OVER. It is the discriminator 82% of senses have: measured
            // on WordNet-3.0, 171,463 of 206,941 senses carry tag_cnt = 0, so their
            // HAS_SENSE magnitude was 0 -> score exactly 0.5 -> a DRAW, and every sense of
            // a lemma folded to an identical rating. That is why a token's senses carry no
            // information for any reader that ranks on the fold (W4 §2.2 measured the
            // 5-way tie on `a` at 994.8; `what` and `the` still measure eff_mu spread 0.0
            // on the live substrate, while chess/pawn/dog measure 95.9/80.4/77.5).
            int senseNumStart = line[idx..].IndexOf((byte)' ');
            if (senseNumStart < 0) continue;
            if (!int.TryParse(
                    System.Text.Encoding.UTF8.GetString(line.Slice(idx, senseNumStart)),
                    out int senseNumber) || senseNumber < 1)
                senseNumber = 0;
            idx += senseNumStart + 1;
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

            string? exactKey = SourceEntityIdConventions.NormalizeExactSenseKey(senseKey);
            if (exactKey is null) continue;
            yield return new WnSense(exactKey, offset, pos, lemma, tagCount, senseNumber);
        }
    }

    internal sealed record WnSynset(
        long Offset, char SsType, int LexFilenum,
        List<string> Lemmas, List<WnPointer> Pointers, string Gloss,
        List<(int Frame, int WordNum)> Frames);

    internal readonly record struct WnPointer(string Symbol, long TargetOffset, char TargetPos, int SrcWord, int TgtWord);

    private sealed record WnSense(
        string SenseKey, long Offset, char Pos, string Lemma, int TagCount, int SenseNumber)
    {
        /// <summary>
        /// Witnessed strength of this lemma->sense claim, from the TWO frequency signals
        /// index.sense ships. tag_cnt (semantic-concordance occurrences) stays dominant
        /// where it exists; sense_number (WordNet's editorial ordering, 1 = most common)
        /// contributes a bounded 1/n term that separates the 82% of senses whose tag_cnt
        /// is 0 and which therefore all folded to the same draw. Both come from the same
        /// witnessed line -- this reads evidence the corpus already shipped, it does not
        /// invent a prior.
        /// </summary>
        public double WitnessedMagnitude =>
            TagCount + (SenseNumber > 0 ? 1.0 / SenseNumber : 0.0);
    };
}
