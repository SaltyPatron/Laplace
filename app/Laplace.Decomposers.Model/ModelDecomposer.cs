using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Deposes a HuggingFace safetensor snapshot (config + tokenizer + weight blobs) into substrate testimony.
/// Not GGUF — safetensors are tensor containers only; recipe and vocab are external files in the snapshot dir.
/// </summary>
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
    public static readonly Hash128 EmbedsTypeId        = RelationTypeRegistry.RelationTypeId("EMBEDS");
    public static readonly Hash128 QProjectsTypeId     = RelationTypeRegistry.RelationTypeId("Q_PROJECTS");
    public static readonly Hash128 KProjectsTypeId     = RelationTypeRegistry.RelationTypeId("K_PROJECTS");
    public static readonly Hash128 VProjectsTypeId     = RelationTypeRegistry.RelationTypeId("V_PROJECTS");
    public static readonly Hash128 OProjectsTypeId     = RelationTypeRegistry.RelationTypeId("O_PROJECTS");
    public static readonly Hash128 GatesTypeId         = RelationTypeRegistry.RelationTypeId("GATES");
    public static readonly Hash128 UpProjectsTypeId    = RelationTypeRegistry.RelationTypeId("UP_PROJECTS");
    public static readonly Hash128 DownProjectsTypeId  = RelationTypeRegistry.RelationTypeId("DOWN_PROJECTS");
    public static readonly Hash128 NormScalesTypeId    = RelationTypeRegistry.RelationTypeId("NORM_SCALES");
    public static readonly Hash128 OutputProjectsTypeId = RelationTypeRegistry.RelationTypeId("OUTPUT_PROJECTS");
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

    public static readonly Hash128 LlamaArchitectureId =
        Hash128.OfCanonical("substrate/entity/Architecture_Llama/v1");

    public static readonly Hash128 ModelAxisTypeId = EntityTypeRegistry.ModelAxis;

    private readonly string _modelDir;
    private readonly Hash128 _source;
    private readonly string  _sourceName;

    public ModelDecomposer(string modelDir)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        (_source, _sourceName) = SourceForModel(modelDir);
    }

    public Hash128 Source       => _source;
    public Hash128 SourceId     => _source;
    public string  SourceName   => _sourceName;
    public int     LayerOrder   => 10;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);

        var recipe = LlamaRecipeExtractor.Parse(Path.Combine(_modelDir, "config.json"));
        var prof = ArchitectureProfile.For(recipe.ModelType);

        boot.AddType("Model_Recipe");
        boot.AddType("Model_Tokenizer");
        boot.AddType("Scalar");
        boot.AddType("Architecture");
        boot.AddType("Ngram");
        boot.AddType("Model_Axis");

        boot.AddRelationType("EMBEDS");
        boot.AddRelationType("Q_PROJECTS");
        boot.AddRelationType("K_PROJECTS");
        boot.AddRelationType("V_PROJECTS");
        boot.AddRelationType("O_PROJECTS");
        boot.AddRelationType("GATES");
        boot.AddRelationType("UP_PROJECTS");
        boot.AddRelationType("DOWN_PROJECTS");
        boot.AddRelationType("NORM_SCALES");
        boot.AddRelationType("MERGES_WITH");
        boot.AddRelationType("OUTPUT_PROJECTS");
        boot.AddType("Neuron");
        boot.AddRelationType("SIMILAR_TO");
        boot.AddRelationType("ATTENDS");
        boot.AddRelationType("OV_RELATES");
        boot.AddRelationType("COMPLETES_TO");
        boot.AddRelationType("DETECTS");
        boot.AddRelationType("WRITES");
        boot.AddRelationType("TOKEN_MAPS_TO");
        boot.AddRelationType("HAS_HIDDEN_SIZE");
        boot.AddRelationType("HAS_NUM_LAYERS");
        boot.AddRelationType("HAS_NUM_HEADS");
        boot.AddRelationType("HAS_NUM_KV_HEADS");
        boot.AddRelationType("HAS_INTERMEDIATE_SIZE");
        boot.AddRelationType("HAS_VOCAB_SIZE");
        boot.AddRelationType("IS_A");

        var seededTypes = new HashSet<Hash128>();
        foreach (var slot in ModelArenaPlan.Slots(recipe, prof))
        {
            if (!seededTypes.Add(slot.RelationTypeId)) continue;
            boot.AddEntity(new EntityRow(slot.RelationTypeId, EntityTier.Vocabulary,
                BootstrapIntentBuilder.RelationTypeMetaTypeId, Source));
            string baseRole = slot.Role.StartsWith("NORM_SCALES", StringComparison.Ordinal)
                ? "NORM_SCALES" : slot.Role;
            Hash128 baseId = ModelArenaPlan.BaseRelationTypeId(baseRole);
            if (baseId != slot.RelationTypeId)
                boot.AddAttestation(NativeAttestation.Categorical(
                    slot.RelationTypeId, "IS_A", baseId, Source, SourceTrust.AiModelProbe));
        }

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

        // LAPLACE_MODEL_PLANES: cells | behavioral | all (default all).
        // cells      = per-(role,layer) arena testimony (what audit-export/synthesize read)
        // behavioral = token<->token planes (SIMILAR_TO/ATTENDS/OV_RELATES/COMPLETES_TO)
        string planes = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES") ?? "all";
        bool doCells = planes is "all" or "cells";
        bool doBehavioral = planes is "all" or "behavioral";

        if (doCells)
        {
            log.LogInformation("phase=cells starting");
            var cells = new ModelCellETL(_modelDir, recipe, tokens, Source, ModelAxisTypeId, log);
            await foreach (var change in cells.EmitAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return change;
            }
        }
        else
            log.LogInformation("phase=cells skipped (LAPLACE_MODEL_PLANES={Planes})", planes);

        if (doBehavioral)
        {
            log.LogInformation("phase=etl starting");
            var etl = new ModelTableETL(_modelDir, recipe, tokens, Source, ModelAxisTypeId,
                epochBase: doCells ? 2 : 0, log);
            await foreach (var change in etl.EmitAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return change;
            }
        }
        else
            log.LogInformation("phase=etl skipped (LAPLACE_MODEL_PLANES={Planes})", planes);

        // Tokenizer→token provenance so a deposited model knows which tokens are its own.
        // Stamped with the highest epoch any ETL stream used (equal epochs are legal).
        int finalEpoch = (doCells ? 2 : 0) + (doBehavioral ? 2 : 0);
        if (finalEpoch > 0) finalEpoch -= 1;
        foreach (var batch in LlamaTokenizerParser.BuildTokenMapsToCategorical(
            tokens, Source, tokEntityId, batchSz, finalEpoch))
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
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
        var p = ArchitectureProfile.For(r.ModelType);
        long d = r.HiddenSize, interm = r.IntermediateSize;
        long headDim = d / r.NumHeads;
        long attnOut = r.NumHeads * headDim, kvDim = (long)r.NumKvHeads * headDim;

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

        long relations =
              distinctVocab * d
            + distinctVocab * d
            + 2 * d * attnOut
            + 2 * d * kvDim
            + (p.HasGate ? d * interm : 0)
            + 2 * d * interm
            + d;
        return distinctVocab + relations;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
