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
            var r = LlamaRecipeExtractor.Parse(configPath);
            return new[]
            {
                LlamaArchitectureCanonical,
                System.Text.Encoding.UTF8.GetString(r.CanonicalJson),
                r.HiddenSize.ToString(), r.NumLayers.ToString(), r.NumHeads.ToString(),
                r.NumKvHeads.ToString(), r.IntermediateSize.ToString(), r.VocabSize.ToString(),
            };
        }
    }

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);

        boot.AddType("Model_Recipe");
        boot.AddType("Model_Tokenizer");
        boot.AddType("Scalar");
        boot.AddType("Architecture");
        boot.AddType("Ngram");
        boot.AddType("Model_Layer");

        boot.AddRelationType("MERGES_WITH");
        boot.AddRelationType("SIMILAR_TO");
        boot.AddRelationType("ATTENDS");
        boot.AddRelationType("OV_RELATES");
        boot.AddRelationType("COMPLETES_TO");
        boot.AddRelationType("TOKEN_MAPS_TO");
        boot.AddRelationType("HAS_HIDDEN_SIZE");
        boot.AddRelationType("HAS_NUM_LAYERS");
        boot.AddRelationType("HAS_NUM_HEADS");
        boot.AddRelationType("HAS_NUM_KV_HEADS");
        boot.AddRelationType("HAS_INTERMEDIATE_SIZE");
        boot.AddRelationType("HAS_VOCAB_SIZE");
        boot.AddRelationType("IS_A");

        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var log = context.Logger;
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        string configPath    = Path.Combine(_modelDir, "config.json");
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");

        var recipe = LlamaRecipeExtractor.Parse(configPath);
        log.LogInformation("phase=recipe parsed: {Layers} layers, {Heads} heads/{Kv} kv, "
            + "d_model={DModel}, vocab={Vocab} ({Ms} ms)",
            recipe.NumLayers, recipe.NumHeads, recipe.NumKvHeads, recipe.HiddenSize,
            recipe.VocabSize, phaseSw.ElapsedMilliseconds);
        await context.Writer.ApplyAsync(LlamaRecipeExtractor.BuildChange(
            recipe, Source, ModelRecipeTypeId,
            HasHiddenSizeTypeId, HasNumLayersTypeId, HasNumHeadsTypeId, HasNumKvHeadsTypeId,
            HasIntermSizeTypeId, HasVocabSizeTypeId,
            IsATypeId, LlamaArchitectureId), ct);

        byte[] tokBytes = File.ReadAllBytes(tokenizerPath);
        var tokEntityId = Hash128.Blake3(tokBytes);
        var tokChange = new SubstrateChangeBuilder(Source, "tokenizer/entity",
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 0);
        tokChange.AddEntity(tokEntityId, EntityTier.Vocabulary, ModelTokenizerTypeId, firstObservedBy: Source);
        await context.Writer.ApplyAsync(tokChange.Build(), ct);

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
            await context.Writer.ApplyAsync(batch, ct);
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
            await context.Writer.ApplyAsync(batch, ct);
            mergeBatches++;
        }
        log.LogInformation("phase=merges emitted: {Count} merges, {Batches} batches ({Ms} ms)",
            merges.Count, mergeBatches, phaseSw.ElapsedMilliseconds);








        const int finalEpoch = 1;
        var edges = new ModelTokenEdgeETL(_modelDir, recipe, tokens, Source, log);
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
        var r = LlamaRecipeExtractor.Parse(configPath);

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
