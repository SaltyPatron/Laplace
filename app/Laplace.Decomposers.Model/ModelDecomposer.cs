using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;





public sealed class ModelDecomposer : IDecomposer, IIngestInventoryProvider
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
    public static readonly Hash128 ModelRecipeTypeId    = EntityTypeRegistry.ModelRecipe;
    public static readonly Hash128 ModelTokenizerTypeId = EntityTypeRegistry.ModelTokenizer;
    public static readonly Hash128 ArchitectureTypeId   = EntityTypeRegistry.Architecture;
    public static readonly Hash128 ScalarTypeId         = EntityTypeRegistry.Scalar;
    public static readonly Hash128 NgramTypeId          = EntityTypeRegistry.Ngram;
    public static readonly Hash128 TokenMapsToTypeId   = RelationTypeRegistry.RelationTypeId("TOKEN_MAPS_TO");
    public static readonly Hash128 SimilarToTypeId     = RelationTypeRegistry.RelationTypeId("SIMILAR_TO");
    public static readonly Hash128 AttendsTypeId       = RelationTypeRegistry.RelationTypeId("ATTENDS");
    public static readonly Hash128 OvRelatesTypeId     = RelationTypeRegistry.RelationTypeId("OV_RELATES");
    public static readonly Hash128 CompletesToTypeId   = RelationTypeRegistry.RelationTypeId("COMPLETES_TO");

    public static readonly Hash128 HasHiddenSizeTypeId  = RelationTypeRegistry.RelationTypeId("HAS_HIDDEN_SIZE");
    public static readonly Hash128 HasNumLayersTypeId   = RelationTypeRegistry.RelationTypeId("HAS_NUM_LAYERS");
    public static readonly Hash128 HasNumHeadsTypeId    = RelationTypeRegistry.RelationTypeId("HAS_NUM_HEADS");
    public static readonly Hash128 HasNumKvHeadsTypeId  = RelationTypeRegistry.RelationTypeId("HAS_NUM_KV_HEADS");
    public static readonly Hash128 HasIntermSizeTypeId  = RelationTypeRegistry.RelationTypeId("HAS_INTERMEDIATE_SIZE");
    public static readonly Hash128 HasVocabSizeTypeId   = RelationTypeRegistry.RelationTypeId("HAS_VOCAB_SIZE");
    public static readonly Hash128 IsATypeId            = RelationTypeRegistry.RelationTypeId("IS_A");

    private static readonly Hash128 LlamaArchitectureId =
        Hash128.OfCanonical("substrate/entity/Architecture_Llama/v1");

    private const string LlamaArchitectureCanonical = "substrate/entity/Architecture_Llama/v1";

    public static readonly Hash128 ModelLayerTypeId = EntityTypeRegistry.ModelLayer;

    private readonly string _modelDir;
    private readonly Hash128 _source;
    private readonly string  _sourceName;
    private readonly bool?   _persistEvidence;

    public ModelDecomposer(string modelDir, bool? persistEvidence = null)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        (_source, _sourceName) = SourceForModel(modelDir);
        _persistEvidence = persistEvidence;
    }

    public Hash128 Source       => _source;
    public Hash128 SourceId     => _source;
    public string  SourceName   => _sourceName;
    public int     LayerOrder   => 10;
    public Hash128 TrustClassId => TrustClass;

    
    
    
    
    
    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            string configPath = Path.Combine(_modelDir, "config.json");
            if (!File.Exists(configPath)) return Array.Empty<string>();
            LlamaRecipeExtractor.RecipeInfo r;
            try { r = LlamaRecipeExtractor.Parse(configPath); }
            catch (Exception) { return Array.Empty<string>(); }
            return new[]
            {
                LlamaArchitectureCanonical,
                System.Text.Encoding.UTF8.GetString(r.CanonicalJson),
                r.HiddenSize.ToString(), r.NumLayers.ToString(), r.NumHeads.ToString(),
                r.NumKvHeads.ToString(), r.IntermediateSize.ToString(), r.VocabSize.ToString(),
            };
        }
    }

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["Model_Recipe", "Model_Tokenizer", "Scalar", "Architecture",
                "Ngram", "Model_Layer", "Model_Circuit"],
            relationNodeNames: ["MERGES_WITH", "SIMILAR_TO", "ATTENDS", "OV_RELATES",
                "COMPLETES_TO", "CONTINUES_TO", "ENCODES", "TOKEN_MAPS_TO",
                "HAS_HIDDEN_SIZE", "HAS_NUM_LAYERS", "HAS_NUM_HEADS", "HAS_NUM_KV_HEADS",
                "HAS_INTERMEDIATE_SIZE", "HAS_VOCAB_SIZE", "IS_A"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var log = context.Logger;
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        string configPath    = Path.Combine(_modelDir, "config.json");
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");

        // ── Lane A: shape-inferred manifest (the frozen contract; never throws) ────────────────
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
                Config = cfgResult.Config, Roles = Array.Empty<TensorRole>(),
                Modality = cfgResult.Modality,
                Coverage = cfgResult.Coverage == Coverage.Full ? Coverage.Partial : cfgResult.Coverage,
                ModelName = _sourceName,
            };
        }
        log.LogInformation("phase=manifest: model_type={Mt} modality={Mod} coverage={Cov} layers={L} roles={R} "
            + "(moe={Moe}, mla={Mla})", manifest.Config.ModelType, manifest.Modality, manifest.Coverage,
            manifest.LayerCount, manifest.Roles.Count, manifest.Config.IsMoe, manifest.Config.IsMla);

        // Legacy HF config-scalar deposit (best effort): keeps the existing HAS_* scalars + IS_A
        // architecture edge for known decoders, but an unknown model_type must never crash ingest.
        SubstrateChange? legacyRecipe = TryBuildLegacyRecipeChange(configPath, log);
        if (legacyRecipe is { } lr)
            yield return lr;

        SubstrateChange? synthRecipe = TryBuildSynthesizedRecipeChange(manifest, log);
        if (synthRecipe is { } sr)
            yield return sr;

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
        var tokChange = new SubstrateChangeBuilder(Source, "tokenizer/entity",
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 0);
        tokChange.AddEntity(tokEntityId, EntityTier.Word, ModelTokenizerTypeId, firstObservedBy: Source);
        yield return tokChange.Build();

        phaseSw.Restart();
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        log.LogInformation("phase=vocab parsed: {Count} tokens ({Ms} ms)",
            tokens.Count, phaseSw.ElapsedMilliseconds);
        int batchSz = Math.Max(options.BatchSize, 8192);
        phaseSw.Restart();
        int vocabBatches = 0;
        foreach (var batch in LlamaTokenizerParser.BuildBatches(
            tokens, Source, TextTypeId, batchSz))
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
        foreach (var batch in LlamaTokenizerParser.BuildMergesBatches(merges, Source, TextTypeId, batchSz))
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            mergeBatches++;
        }
        log.LogInformation("phase=merges emitted: {Count} merges, {Batches} batches ({Ms} ms)",
            merges.Count, mergeBatches, phaseSw.ElapsedMilliseconds);








        const int finalEpoch = 1;
        // Lane C decoder ring: cross-reference each circuit's strongest pairs against seed knowledge.
        var classifier = new HeadClassifier(context.Reader, Source, _sourceName, log);
        var edges = new ModelTokenEdgeETL(_modelDir, manifest, tokens, Source, log, classifier);
        await foreach (var change in edges.EmitAsync(finalEpoch, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
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

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
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
        catch (Exception) { return 0; }

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
            catch (Exception)
            {
            }
        }

        long partners = Math.Min(distinctVocab, 64);
        long perLayerPlanes = 3L * distinctVocab * partners * r.NumLayers;
        long similarTo = distinctVocab * partners;
        return distinctVocab + perLayerPlanes + similarTo;
    }

    private SubstrateChange? TryBuildLegacyRecipeChange(string configPath, ILogger log)
    {
        try
        {
            var recipe = LlamaRecipeExtractor.Parse(configPath);
            return LlamaRecipeExtractor.BuildChange(
                recipe, Source, ModelRecipeTypeId,
                HasHiddenSizeTypeId, HasNumLayersTypeId, HasNumHeadsTypeId, HasNumKvHeadsTypeId,
                HasIntermSizeTypeId, HasVocabSizeTypeId,
                IsATypeId, LlamaArchitectureId);
        }
        catch (Exception ex)
        {
            log.LogWarning("phase=recipe: legacy config-scalar deposit skipped ({Msg})", ex.Message);
            return null;
        }
    }

    private SubstrateChange? TryBuildSynthesizedRecipeChange(ModelManifest manifest, ILogger log)
    {
        try
        {
            var synth = RecipeSynthesizer.Synthesize(manifest);
            log.LogInformation("phase=recipe: synthesized laplace.recipe ({Layers} layers) deposited", synth.NumLayers);
            return RecipeExtractor.BuildChange(
                synth, Source, ModelRecipeTypeId, HasHiddenSizeTypeId, HasNumLayersTypeId);
        }
        catch (Exception ex)
        {
            log.LogWarning("phase=recipe: recipe synthesis skipped ({Msg})", ex.Message);
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
