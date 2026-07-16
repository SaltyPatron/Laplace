using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;





public sealed class ModelDecomposer : DecomposerMultiPhase, IIngestInventoryProvider
{
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AIModelProbe/v1");

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

    public static readonly Hash128 HasHiddenSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_HIDDEN_SIZE");
    public static readonly Hash128 HasNumLayersTypeId = RelationTypeRegistry.RelationTypeId("HAS_NUM_LAYERS");
    public static readonly Hash128 HasNumHeadsTypeId = RelationTypeRegistry.RelationTypeId("HAS_NUM_HEADS");
    public static readonly Hash128 HasNumKvHeadsTypeId = RelationTypeRegistry.RelationTypeId("HAS_NUM_KV_HEADS");
    public static readonly Hash128 HasIntermSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_INTERMEDIATE_SIZE");
    public static readonly Hash128 HasVocabSizeTypeId = RelationTypeRegistry.RelationTypeId("HAS_VOCAB_SIZE");
    public static readonly Hash128 IsATypeId = RelationTypeRegistry.RelationTypeId("IS_A");

    private static readonly Hash128 LlamaArchitectureId =
        Hash128.OfCanonical("substrate/entity/Architecture_Llama/v1");

    private const string LlamaArchitectureCanonical = "substrate/entity/Architecture_Llama/v1";

    public static readonly Hash128 ModelLayerTypeId = EntityTypeRegistry.ModelLayer;

    // Analyzer watermark, chess parity (ChessVocabulary.AnalysisMarkerId): one
    // deterministic marker per (model source, planes mode, analyzer version).
    // The analyzer probes it to skip an already-derived pass (a re-run would
    // double-fold consensus) and deposits it as its final change. Bump the
    // version to evict + re-derive without touching the witnessed layer.
    public static readonly Hash128 AnalysisMarkerTypeId = EntityTypeRegistry.Id("Model_AnalysisMarker");
    // v2: v1 marker on hart-desktop was deposited by a stale CLI tree that ran
    // planes mode 'factors' before the factors branch existed (derived nothing).
    // v3: factor-trajectory layout v2 (FactorTrajectory law — arena vertex +
    // per-token testimony headers + all planes q/k/ov/mlp/emb); v2 rows were
    // q/k-only anonymous layout, superseded and evictable.
    // v4: slice -> circuit-coordinate APPEARS_IN anatomy links added to the
    // factors pass (SQL navigation); trajectories unchanged (insert-if-absent
    // skips them on re-run — only the anatomy attestations are novel).
    internal const int AnalyzerVersion = 4;

    // Ledger §6 step 1 — the checkpoint as CONTENT — lives in ModelCheckpoint
    // (Blake3 of literal tensor byte ranges → Merkle root, CONTAINS/PRECEDES),
    // mirroring ModelCoordinates' structure law. The model never enters an id.

    public static Hash128 AnalysisMarkerId(Hash128 modelSource, string planesMode)
        => Hash128.OfCanonical($"model/analyzed/{modelSource}/{planesMode}/v{AnalyzerVersion}");

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






    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            string configPath = Path.Combine(_modelDir, "config.json");
            if (!File.Exists(configPath)) return Array.Empty<string>();
            LlamaRecipeExtractor.RecipeInfo r;
            try { r = LlamaRecipeExtractor.Parse(configPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "ModelDecomposer: config parse failed for readback: {Message}", ex.Message);
                return Array.Empty<string>();
            }
            return new[]
            {
                LlamaArchitectureCanonical,
                System.Text.Encoding.UTF8.GetString(r.CanonicalJson),
                r.HiddenSize.ToString(), r.NumLayers.ToString(), r.NumHeads.ToString(),
                r.NumKvHeads.ToString(), r.IntermediateSize.ToString(), r.VocabSize.ToString(),
            };
        }
    }

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


        var cfgResult = ModelConfigReader.Read(configPath);
        ModelManifest manifest;
        try
        {
            var headers = SafetensorsContainerParser.ParseModel(_modelDir);
            manifest = TensorRoleClassifier.Build(headers, cfgResult, _sourceName);
        }
        catch (Exception ex)
        {
            log.LogWarning("phase=manifest: tensor headers unavailable ({Msg}); recipe-only path", ex.Message);
            manifest = new ModelManifest
            {
                Config = cfgResult.Config,
                Roles = Array.Empty<TensorRole>(),
                Modality = cfgResult.Modality,
                Coverage = cfgResult.Coverage == Coverage.Full ? Coverage.Partial : cfgResult.Coverage,
                ModelName = _sourceName,
            };
        }
        log.LogInformation("phase=manifest: model_type={Mt} modality={Mod} coverage={Cov} layers={L} roles={R} "
            + "(moe={Moe}, mla={Mla})", manifest.Config.ModelType, manifest.Modality, manifest.Coverage,
            manifest.LayerCount, manifest.Roles.Count, manifest.Config.IsMoe, manifest.Config.IsMla);



        // Analyzer modes (LAPLACE_MODEL_PLANES != "structure") deposit CALCULATED
        // planes only. The witnessed phases (recipe/tokenizer/vocab/merges/maps-to)
        // belong to the recorder and must not re-fold their witnesses on re-runs.
        bool recorderRun = ModelTokenEdgeETL.ResolvePlanesMode() == "structure";

        if (recorderRun)
        {
            await foreach (var batch in RunPhaseAsync(new LegacyRecipePhase(this, configPath, log), context, options, ct))
                yield return batch;

            await foreach (var batch in RunPhaseAsync(new SynthRecipePhase(this, manifest, log), context, options, ct))
                yield return batch;

            // §6 step 1 — the checkpoint as content: tensor byte-range identities +
            // Merkle root + CONTAINS/PRECEDES structure. Recipe-only models (no
            // weight blobs) skip it; a single deposit, insert-if-absent.
            SubstrateChange? checkpointChange = null;
            try
            {
                var tensors = SafetensorsContainerParser.ParseModel(_modelDir);
                if (tensors.Count > 0)
                {
                    var cb = new SubstrateChangeBuilder(_source, "checkpoint/byte-ranges", null,
                        entityCapacity: tensors.Count + 1, physicalityCapacity: 0,
                        attestationCapacity: 2 * tensors.Count);
                    var root = ModelCheckpoint.StageCheckpoint(cb, tensors, _source);
                    checkpointChange = cb.Build();
                    log.LogInformation("phase=checkpoint: {Tensors} tensor byte-ranges deposited, root={Root}",
                        tensors.Count, Convert.ToHexString(root.ToBytes()).ToLowerInvariant()[..16]);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("phase=checkpoint: byte-range deposit skipped ({Msg})", ex.Message);
            }
            if (checkpointChange is not null)
                yield return checkpointChange;
        }

        if (manifest.Coverage == Coverage.Unsupported)
        {
            log.LogWarning("phase=ingest: model '{Name}' unsupported; recipe scalars deposited, no circuit decrypt",
                _sourceName);
            yield break;
        }
        if (!File.Exists(tokenizerPath))
        {
            log.LogWarning("phase=ingest: no tokenizer.json for '{Name}' (modality={Mod}); recipe-only ingest",
                _sourceName, manifest.Modality);
            yield break;
        }

        byte[] tokBytes = File.ReadAllBytes(tokenizerPath);
        var tokEntityId = Hash128.Blake3(tokBytes);
        phaseSw.Restart();
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        log.LogInformation("phase=vocab parsed: {Count} tokens ({Ms} ms)",
            tokens.Count, phaseSw.ElapsedMilliseconds);
        int batchSz = Math.Max(options.BatchSize, 8192);

        if (recorderRun)
        {
            await foreach (var batch in RunPhaseAsync(new TokenizerEntityPhase(this, tokEntityId), context, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return batch;
            }

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

            int mapsBatches = 0;
            await foreach (var batch in RunPhaseAsync(
                               new MapsToPhase(this, tokens, tokEntityId, batchSz), context, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return batch;
                mapsBatches++;
            }
            log.LogInformation("phase=maps-to emitted: {Batches} batches ({Ms} ms)",
                mapsBatches, phaseSw.ElapsedMilliseconds);
        }
        else
        {
            log.LogInformation("phase=analyzer: planes mode '{Mode}' — witnessed phases skipped",
                ModelTokenEdgeETL.ResolvePlanesMode());
        }








        const int finalEpoch = 1;

        // Analyzer watermark (chess FilterUnanalyzedAsync parity): a completed
        // pass at this (source, mode, version) must not re-fold its planes.
        string planesMode = ModelTokenEdgeETL.ResolvePlanesMode();
        Hash128 analysisMarker = AnalysisMarkerId(_source, planesMode);
        if (!recorderRun)
        {
            byte[] bm = await context.Reader.EntitiesExistBitmapAsync([analysisMarker], ct);
            if (bm.Length > 0 && (bm[0] & 1) != 0)
            {
                log.LogInformation(
                    "phase=analyzer: planes mode '{Mode}' already derived at v{V} for '{Name}' "
                    + "— skipping (a re-run would double-fold; bump AnalyzerVersion or evict the marker to re-derive)",
                    planesMode, AnalyzerVersion, _sourceName);
                yield break;
            }
        }

        var classifier = new HeadClassifier(context.Reader, Source, log);
        var edges = new ModelTokenEdgeETL(_modelDir, manifest, tokens, Source, log, classifier);
        await foreach (var change in edges.EmitAsync(finalEpoch, context.Reader, options, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }

        if (!recorderRun)
        {
            var mb = new SubstrateChangeBuilder(_source, $"model/analysis-marker/{planesMode}", null,
                entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 0);
            mb.AddEntity(analysisMarker, EntityTier.Word, AnalysisMarkerTypeId, firstObservedBy: _source);
            yield return mb.Build();
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
        LlamaRecipeExtractor.RecipeInfo r;
        try { r = LlamaRecipeExtractor.Parse(configPath); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ModelDecomposer: config parse failed for unit estimate: {Message}", ex.Message);
            return 0;
        }

        long distinctVocab = r.VocabSize;
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
                    "ModelDecomposer: tokenizer parse failed for unit estimate: {Message}", ex.Message);
            }
        }

        // Matches ModelTokenEdgeETL's untruncated emission for the active mode:
        // every token per circuit in structure mode; every pair per plane in
        // analyzer modes (identical ids merge across circuits at apply).
        if (ModelTokenEdgeETL.ResolvePlanesMode() == "structure")
        {
            // Per layer: attention + OV heads, plus either one dense MLP circuit or
            // one router coordinate per expert.
            int numExperts = 0;
            try { numExperts = ModelConfigReader.Read(configPath).Config.NumExperts; }
            catch (Exception) { }
            long mlpSlots = Math.Max(1, numExperts);
            long circuits = (2L * Math.Max(1, r.NumHeads) + mlpSlots) * r.NumLayers;
            long occurrences = circuits * distinctVocab;
            return distinctVocab + occurrences;
        }
        long perLayerPlanes = 3L * distinctVocab * distinctVocab * r.NumLayers;
        long similarTo = distinctVocab * distinctVocab;
        return distinctVocab + perLayerPlanes + similarTo;
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
                    SourceId, BatchLabelPrefix, _batch, options, context.Reader, PipelineProfile),
                options);
    }

    private sealed class LegacyRecipePhase : ModelComposePhase<LlamaRecipeExtractor.RecipeInfo>
    {
        private readonly string _configPath;
        private readonly ILogger _log;
        private LlamaRecipeExtractor.RecipeInfo? _recipe;
        private bool _parsed;

        public LegacyRecipePhase(ModelDecomposer owner, string configPath, ILogger log) : base(owner, 1)
        {
            _configPath = configPath;
            _log = log;
        }

        protected override string PhaseLabel => "recipe/config.json";

        protected override void Compose(LlamaRecipeExtractor.RecipeInfo rec, SubstrateChangeBuilder b) =>
            LlamaRecipeExtractor.StageLegacyRecipe(
                b, rec, SourceId, ModelRecipeTypeId,
                HasHiddenSizeTypeId, HasNumLayersTypeId, HasNumHeadsTypeId, HasNumKvHeadsTypeId,
                HasIntermSizeTypeId, HasVocabSizeTypeId, IsATypeId, LlamaArchitectureId);

        protected override async IAsyncEnumerable<LlamaRecipeExtractor.RecipeInfo> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (!_parsed)
            {
                _parsed = true;
                try { _recipe = LlamaRecipeExtractor.Parse(_configPath); }
                catch (Exception ex)
                {
                    _log.LogWarning("phase=recipe: legacy config-scalar deposit skipped ({Msg})", ex.Message);
                    yield break;
                }
            }
            if (_recipe is not null)
            {
                ct.ThrowIfCancellationRequested();
                yield return _recipe;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class SynthRecipePhase : ModelComposePhase<RecipeExtractor.RecipeInfo>
    {
        private readonly ModelManifest _manifest;
        private readonly ILogger _log;
        private RecipeExtractor.RecipeInfo? _recipe;
        private bool _synthesized;

        public SynthRecipePhase(ModelDecomposer owner, ModelManifest manifest, ILogger log) : base(owner, 1)
        {
            _manifest = manifest;
            _log = log;
        }

        protected override string PhaseLabel => "recipe/laplace.recipe";

        protected override void Compose(RecipeExtractor.RecipeInfo rec, SubstrateChangeBuilder b) =>
            RecipeExtractor.StageRecipe(b, rec, SourceId, ModelRecipeTypeId, HasHiddenSizeTypeId, HasNumLayersTypeId);

        protected override async IAsyncEnumerable<RecipeExtractor.RecipeInfo> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (!_synthesized)
            {
                _synthesized = true;
                try
                {
                    _recipe = RecipeSynthesizer.Synthesize(_manifest);
                    _log.LogInformation("phase=recipe: synthesized laplace.recipe ({Layers} layers) deposited",
                        _recipe.NumLayers);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("phase=recipe: recipe synthesis skipped ({Msg})", ex.Message);
                    yield break;
                }
            }
            if (_recipe is not null)
            {
                ct.ThrowIfCancellationRequested();
                yield return _recipe;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class TokenizerEntityPhase : ModelComposePhase<Hash128>
    {
        private readonly Hash128 _tokEntityId;

        public TokenizerEntityPhase(ModelDecomposer owner, Hash128 tokEntityId) : base(owner, 1)
            => _tokEntityId = tokEntityId;

        protected override string PhaseLabel => "tokenizer/entity";

        protected override void Compose(Hash128 id, SubstrateChangeBuilder b) =>
            b.AddEntity(id, EntityTier.Word, ModelTokenizerTypeId, firstObservedBy: SourceId);

        protected override async IAsyncEnumerable<Hash128> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return _tokEntityId;
            await Task.CompletedTask;
        }
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

    private sealed class MapsToPhase : ModelComposePhase<LlamaTokenizerParser.TokenMapsToRecord>
    {
        private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
        private readonly Hash128 _tokEntityId;

        public MapsToPhase(
            ModelDecomposer owner,
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
            Hash128 tokEntityId,
            int batch) : base(owner, batch)
        {
            _tokens = tokens;
            _tokEntityId = tokEntityId;
        }

        protected override string PhaseLabel => "tokenizer/maps-to";

        protected override void Compose(LlamaTokenizerParser.TokenMapsToRecord rec, SubstrateChangeBuilder b) =>
            LlamaTokenizerParser.StageMapsToRecord(b, rec, SourceId);

        protected override async IAsyncEnumerable<LlamaTokenizerParser.TokenMapsToRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in LlamaTokenizerParser.EnumerateMapsToRecordsAsync(_tokens, _tokEntityId, ct))
                yield return rec;
        }
    }
}
