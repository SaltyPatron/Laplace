using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;





public sealed class ModelDecomposer : DecomposerMultiPhase, IIngestInventoryProvider
{
    public static readonly Hash128 TrustClass =
        TrustClassRegistry.Id("AIModelProbe");

    public static (Hash128 Id, string Name) SourceForModel(string modelDir)
    {
        string name = DeriveModelName(modelDir);
        Hash128 id = SourceEntityIdConventions.ModelContentSourceId(modelDir)
                     ?? Hash128.OfCanonical($"substrate/source/{name}/v1");
        return (id, name);
    }

    private static string DeriveModelName(string modelDir)
    {
        string norm = (modelDir ?? "").Replace('\\', '/').TrimEnd('/');
        var segs = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segs)
            if (seg.StartsWith("models--", StringComparison.Ordinal))
                return string.Join("/", seg.Substring("models--".Length).Split("--"));
        for (int i = segs.Length - 1; i >= 0; i--)
        {
            string s = segs[i];
            if (s == "snapshots") continue;
            if (s.Length >= 32 && s.All(Uri.IsHexDigit)) continue;
            return s;
        }
        return "model";
    }

    public static readonly Hash128 TextTypeId = TextEntityBuilder.WordTypeId;
    public static readonly Hash128 ModelRecipeTypeId = EntityTypeRegistry.ModelRecipe;
    public static readonly Hash128 ModelTokenizerTypeId = EntityTypeRegistry.ModelTokenizer;
    public static readonly Hash128 ArchitectureTypeId = EntityTypeRegistry.Architecture;
    public static readonly Hash128 ScalarTypeId = EntityTypeRegistry.Scalar;
    public static readonly Hash128 NgramTypeId = EntityTypeRegistry.Ngram;
    public static readonly Hash128 TokenMapsToTypeId = RelationTypeRegistry.RelationTypeId("TOKEN_MAPS_TO");
    public static readonly Hash128 SimilarToTypeId = RelationTypeRegistry.RelationTypeId("SIMILAR_TO");
    public static readonly Hash128 AttendsTypeId = RelationTypeRegistry.RelationTypeId("ATTENDS");
    public static readonly Hash128 OvRelatesTypeId = RelationTypeRegistry.RelationTypeId("OV_RELATES");
    public static readonly Hash128 CompletesToTypeId = RelationTypeRegistry.RelationTypeId("COMPLETES_TO");


    public static readonly Hash128 ModelLayerTypeId = EntityTypeRegistry.ModelLayer;

    // Analyzer watermark, chess parity (ChessVocabulary.AnalysisMarkerId): one
    // deterministic marker per (model source, planes mode, analyzer version).
    // The analyzer probes it to skip an already-derived pass (a re-run would
    // double-fold consensus) and deposits it as its final change. Bump the
    // version to evict + re-derive without touching the witnessed layer.
    public static readonly Hash128 AnalysisMarkerTypeId = EntityTypeRegistry.Id("Model_AnalysisMarker");
    public static readonly Hash128 AnalysisPendingTypeId = EntityTypeRegistry.Id("Model_AnalysisPending");
    // v6 retires the uncalibrated top-salience evidence lane. The source retains
    // ordered header provenance only until a governed token-pair contraction
    // can witness actual outcomes.
    // Checkpoint provenance lives in ModelCheckpoint as native ordered header
    // structure. Numeric values remain transient input to native contraction.

    public static Hash128 AnalysisMarkerId(Hash128 modelSource, string planesMode)
        => Hash128.OfCanonical(
            $"model/analyzed/{modelSource}/{planesMode}/v{ModelTokenEdgeETL.AnalyzerVersion}");

    private readonly string _modelDir;
    private readonly Hash128 _source;
    private readonly string _sourceName;
    private readonly bool? _persistEvidence;

    public ModelDecomposer(string modelDir, bool? persistEvidence = null)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        (_source, _sourceName) = SourceForModel(modelDir);
        _persistEvidence = persistEvidence;
    }

    public Hash128 Source => _source;
    public override Hash128 SourceId => _source;
    public override string SourceName => _sourceName;
    public override int LayerOrder => 10;
    public override Hash128 TrustClassId => TrustClass;






    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterManifestAsync(
            context, new ModelRuntimeManifest(Source, SourceName), ct: ct);

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var log = context.Logger;
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        string configPath = Path.Combine(_modelDir, "config.json");
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");


        // Recognition reads the header shapes, the config and the tokenizer's id
        // space only, before anything is written: an architecture the templates do
        // not cover is reported as unrecognized structure, never failed mid-run.
        var cfgResult = ModelConfigReader.Read(configPath);
        int? tokenizerIds = null;
        if (File.Exists(tokenizerPath))
        {
            try { tokenizerIds = LlamaTokenizerParser.IdSpace(File.ReadAllBytes(tokenizerPath)); }
            catch (Exception ex) { log.LogWarning("phase=manifest: tokenizer id space unreadable ({Msg})", ex.Message); }
        }
        IReadOnlyList<SafetensorsContainerParser.TensorReference> headers;
        try { headers = SafetensorsContainerParser.ParseModel(_modelDir); }
        catch (Exception ex)
        {
            log.LogWarning("phase=manifest: tensor headers unavailable ({Msg})", ex.Message);
            headers = Array.Empty<SafetensorsContainerParser.TensorReference>();
        }
        ModelManifest manifest = ModelManifest.Recognize(headers, cfgResult, tokenizerIds, _sourceName);
        ModelAnatomy anatomy = manifest.Anatomy;
        log.LogInformation(
            "phase=manifest: tensors={Tensors} operators={Operators} ambiguous_slots={Ambiguous} unrecognized={Unrecognized} conflicts={Conflicts} modality={Mod} coverage={Cov} "
            + "d={D} V={V} L={L} h={H} h_kv={Hkv} d_h={Dh} f={F}",
            anatomy.TensorCount, anatomy.Operators.Count,
            anatomy.Operators.Sum(o => o.Slots.Count(s => s.IsAmbiguous)),
            anatomy.Unrecognized.Count, anatomy.Conflicts.Count, manifest.Modality, manifest.Coverage,
            anatomy.Symbol("d"), anatomy.Symbol("V"), anatomy.Symbol("L"), anatomy.Symbol("h"),
            anatomy.Symbol("h_kv"), anatomy.Symbol("d_h"), anatomy.Symbol("f"));
        foreach (HeaderTensor t in anatomy.Unrecognized)
            log.LogInformation("phase=manifest: unrecognized tensor {Name} [{Shape}]", t.Name, string.Join(",", t.Shape));
        foreach (string conflict in anatomy.Conflicts)
            log.LogWarning("phase=manifest: {Conflict}", conflict);

        // Model ingest is one source-decomposition pass. Checkpoint/tokenizer/
        // config structure is admitted normally; numeric tensor payloads are only
        // transient operands used to derive source-scoped circuit physicalities
        // and typed evidence. No prompt execution or raw-weight persistence occurs.
        bool recorderRun = ModelTokenEdgeETL.ResolvePlanesMode() == "structure";

        if (recorderRun && headers.Count > 0)
        {
            // Ordered safetensors header structure is the checkpoint provenance, and
            // the config's declared dimensions are structured facts about it.
            var cb = new SubstrateChangeBuilder(_source, "checkpoint/structure", null,
                    entityCapacity: headers.Count + 8, physicalityCapacity: 0,
                    attestationCapacity: 8)
                .DeclareSourcePrior(Abstractions.SourceTrust.AiModelProbe);
            Hash128 root = ModelCheckpoint.StageCheckpoint(cb, headers, _source);
            int facts = StageConfigFacts(cb, root, cfgResult.IntegerFields);
            yield return cb.Build();
            log.LogInformation("phase=checkpoint: {Tensors} tensor headers and {Facts} config facts deposited, root={Root}",
                headers.Count, facts, Convert.ToHexString(root.ToBytes()).ToLowerInvariant()[..16]);
        }

        if (manifest.Coverage == Coverage.Unsupported)
        {
            log.LogWarning("phase=ingest: model '{Name}' unsupported; header structure and config facts deposited, no circuits",
                _sourceName);
            yield break;
        }
        if (!File.Exists(tokenizerPath))
        {
            log.LogWarning("phase=ingest: no tokenizer.json for '{Name}' (modality={Mod}); recipe-only ingest",
                _sourceName, manifest.Modality);
            yield break;
        }

        phaseSw.Restart();
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        log.LogInformation("phase=vocab parsed: {Count} tokens ({Ms} ms)",
            tokens.Count, phaseSw.ElapsedMilliseconds);
        int batchSz = IngestPipelineDefaults.ResolveBatch(
            IngestSourceProfile.Default, options);

        if (recorderRun)
        {
            phaseSw.Restart();
            int vocabBatches = 0;
            await foreach (var batch in RunPhaseAsync(new VocabPhase(this, tokens, batchSz), context, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return batch;
                vocabBatches++;
            }
            log.LogInformation("phase=vocab emitted: {Batches} batches ({Ms} ms)",
                vocabBatches, phaseSw.ElapsedMilliseconds);

            phaseSw.Restart();
            var merges = LlamaTokenizerParser.ParseMerges(tokenizerPath);
            int mergeBatches = 0;
            await foreach (var batch in RunPhaseAsync(new MergesPhase(this, merges, batchSz), context, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return batch;
                mergeBatches++;
            }
            log.LogInformation("phase=merges emitted: {Count} merges, {Batches} batches ({Ms} ms)",
                merges.Count, mergeBatches, phaseSw.ElapsedMilliseconds);

            var vb = new SubstrateChangeBuilder(_source, "tokenizer/vocabulary", null,
                    entityCapacity: 1, physicalityCapacity: 1, attestationCapacity: 0)
                .DeclareSourcePrior(Abstractions.SourceTrust.AiModelProbe);
            if (LlamaTokenizerParser.StageVocabulary(vb, tokens, _source) is { } vocabularyId)
            {
                yield return vb.Build();
                log.LogInformation("phase=tokenizer-vocabulary: {Count} pieces ordered by model-local id, vocabulary={Id}",
                    tokens.Count, Convert.ToHexString(vocabularyId.ToBytes()).ToLowerInvariant()[..16]);
            }
            else
            {
                log.LogWarning("phase=tokenizer-vocabulary: token ids are not dense; model-local ids are not retained as ordinals");
            }
        }
        else
        {
            log.LogInformation("phase=analyzer: planes mode '{Mode}' — witnessed phases skipped",
                ModelTokenEdgeETL.ResolvePlanesMode());
        }








        if (recorderRun)
        {
            var contraction = new ModelTokenEdgeETL(_modelDir, manifest, tokens, Source, _sourceName, log);
            await foreach (var change in contraction.EmitAsync(1, context.Reader, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return change;
            }
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!SafetensorSnapshotWitness.IsComplete(_modelDir))
            return Task.FromResult<IngestInventory?>(null);

        var files = new List<IngestFileSpec>();
        foreach (var path in Directory.GetFiles(_modelDir, "*.safetensors").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            int tensors = SafetensorsContainerParser.ParseHeader(path).Count;
            files.Add(new(Path.GetFileName(path), path, tensors));
        }

        long matchups = EstimateMatchupUnits();
        if (matchups <= 0) matchups = 1;
        return Task.FromResult<IngestInventory?>(new("matchups", matchups, files));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EstimateMatchupUnits();
        return Task.FromResult<long?>(n > 0 ? n : null);
    }





    private long EstimateMatchupUnits()
    {
        string configPath = Path.Combine(_modelDir, "config.json");
        if (!File.Exists(configPath)) return 0;
        long distinctVocab = ModelConfigReader.Read(configPath).Config.VocabSize;
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");
        if (File.Exists(tokenizerPath))
        {
            try
            {
                var ids = new HashSet<Hash128>();
                foreach (var t in LlamaTokenizerParser.Parse(tokenizerPath)) ids.Add(t.EntityId);
                distinctVocab = ids.Count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"ModelDecomposer: tokenizer parse failed for unit estimate: {ex.Message}");
            }
        }

        // The admitted source consists of tokenizer/content structure plus the
        // ordered safetensors header and derived circuit forms. Raw tensor values
        // are not durable ingest units; the circuit decomposer consumes them
        // transiently under the source snapshot.
        long headerTensors = 0;
        foreach (string path in Directory.GetFiles(_modelDir, "*.safetensors"))
        {
            try { headerTensors += SafetensorsContainerParser.ParseHeader(path).Count; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"ModelDecomposer: safetensors header parse failed for unit estimate: {ex.Message}");
            }
        }
        return checked(distinctVocab + Math.Max(1, headerTensors));
    }

    private abstract class ModelComposePhase<T> : ComposeDecomposerPhase<T>
    {
        protected readonly ModelDecomposer Owner;
        private readonly int _batch;

        protected ModelComposePhase(ModelDecomposer owner, int batch)
        {
            Owner = owner;
            _batch = batch;
        }

        public override Hash128 SourceId => Owner.SourceId;
        public override string SourceName => Owner.SourceName;
        public override int LayerOrder => Owner.LayerOrder;
        public override Hash128 TrustClassId => Owner.TrustClassId;
        protected override double SourceTrust => Abstractions.SourceTrust.AiModelProbe;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId, BatchLabelPrefix, options, context.Reader, PipelineProfile),
                options);
    }

    private sealed class VocabPhase : ModelComposePhase<LlamaTokenizerParser.TokenRecord>
    {
        private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;

        public VocabPhase(ModelDecomposer owner, IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, int batch)
            : base(owner, batch) => _tokens = tokens;

        protected override string PhaseLabel => "tokenizer/vocab";

        protected override void Compose(LlamaTokenizerParser.TokenRecord rec, SubstrateChangeBuilder b) =>
            LlamaTokenizerParser.StageVocabToken(b, rec, SourceId);

        protected override async IAsyncEnumerable<LlamaTokenizerParser.TokenRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in LlamaTokenizerParser.EnumerateVocabRecordsAsync(_tokens, ct))
                yield return rec;
        }
    }

    private sealed class MergesPhase : ModelComposePhase<LlamaTokenizerParser.MergeRecord>
    {
        private readonly List<(byte[] Left, byte[] Right)> _merges;

        public MergesPhase(ModelDecomposer owner, List<(byte[] Left, byte[] Right)> merges, int batch)
            : base(owner, batch) => _merges = merges;

        protected override string PhaseLabel => "tokenizer/merges";

        protected override void Compose(LlamaTokenizerParser.MergeRecord rec, SubstrateChangeBuilder b) =>
            LlamaTokenizerParser.StageMergeRecord(b, rec, SourceId, TextTypeId);

        protected override async IAsyncEnumerable<LlamaTokenizerParser.MergeRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in LlamaTokenizerParser.EnumerateMergeRecordsAsync(_merges, ct))
                yield return rec;
        }
    }

    // The config's declared dimensions, as the model witness states them about its
    // checkpoint structure: each is the property value [config key, value] under
    // HAS_ATTRIBUTE. The key is the config's own vocabulary, never a relation of its own;
    // the config file is not minted as a recipe entity of its own.
    private static readonly string[] DeclaredRelations = ["HAS_ATTRIBUTE"];
    private static string HasAttribute => DeclaredRelations[0];

    private static readonly string[] ConfigFacts =
    [
        "hidden_size", "num_hidden_layers", "num_attention_heads", "num_key_value_heads",
        "intermediate_size", "vocab_size",
    ];

    private int StageConfigFacts(
        SubstrateChangeBuilder b, Hash128 checkpoint, IReadOnlyDictionary<string, long> config)
    {
        int written = 0;
        foreach (string key in ConfigFacts)
        {
            if (!config.TryGetValue(key, out long value)) continue;
            Hash128 fact = ContentEmitter.StagePropertyValue(
                    b, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), _source)
                ?? throw new InvalidOperationException($"config value {key}={value} has no content root");
            b.AddAttestation(NativeAttestation.Categorical(
                checkpoint, HasAttribute, fact, _source, null, 1.0));
            written++;
        }
        return written;
    }
}
