using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

public sealed class ModelDecomposer : IDecomposer
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

    public static readonly Hash128 TextTypeId =
        Hash128.OfCanonical("substrate/type/Text/v1");
    public static readonly Hash128 ModelRecipeTypeId =
        Hash128.OfCanonical("substrate/type/Model_Recipe/v1");
    public static readonly Hash128 ModelTokenizerTypeId =
        Hash128.OfCanonical("substrate/type/Model_Tokenizer/v1");
    public static readonly Hash128 ArchitectureTypeId =
        Hash128.OfCanonical("substrate/type/Architecture/v1");
    public static readonly Hash128 ScalarTypeId =
        Hash128.OfCanonical("substrate/type/Scalar/v1");
    public static readonly Hash128 NgramTypeId =
        Hash128.OfCanonical("substrate/type/Ngram/v1");
    public static readonly Hash128 ModelAxisTypeId =
        Hash128.OfCanonical("substrate/type/Model_Axis/v1");

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

        boot.AddType("Text");

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

        var seededKinds = new HashSet<Hash128>();
        foreach (var slot in ModelArenaPlan.Slots(recipe, prof))
        {
            if (!seededKinds.Add(slot.KindId)) continue;
            boot.AddEntity(new EntityRow(slot.KindId, (byte)MetaTier.RelationType,
                BootstrapIntentBuilder.RelationTypeMetaTypeId, Source));
            string baseRole = slot.Role.StartsWith("NORM_SCALES", StringComparison.Ordinal)
                ? "NORM_SCALES" : slot.Role;
            Hash128 baseId = ModelArenaPlan.BaseKindId(baseRole);
            if (baseId != slot.KindId)
                boot.AddAttestation(RelationTypeRegistry.Attest(
                    slot.KindId, "IS_A", baseId, Source, SourceTrust.AiModelProbe));
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
        tokChange.AddEntity(tokEntityId, (byte)MetaTier.Meta, ModelTokenizerTypeId, firstObservedBy: Source);
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

        log.LogInformation("phase=etl starting (weight tables → adjudicated matches under tensor-role kinds)");
        var etl = new ModelTableETL(_modelDir, recipe, tokens, Source, ModelAxisTypeId, log);
        await foreach (var change in etl.EmitAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (change.Metadata.SourceContentUnitName == "model/axes")
            {
                await context.Writer.ApplyAsync(change, ct);
                continue;
            }
            yield return change;
        }

        bool morphEmitted = false;
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_MORPH") == "1")
        {
            log.LogInformation("phase=S3-morph SKIPPED (LAPLACE_SKIP_MORPH=1)");
        }
        else
        {
        log.LogInformation("phase=S3-morph starting (embed_tokens → Unicode S³ frame)");
        var morph = new WeightTensorETL(_modelDir, recipe, tokens, Source, tokEntityId, log);
        await foreach (var change in morph.EmitS3MorphAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            morphEmitted = true;
            yield return change;
        }
        }
        if (!morphEmitted)
        {
            log.LogInformation("phase=token-maps-to: categorical fallback (morph skipped/degenerate)");
            foreach (var batch in LlamaTokenizerParser.BuildTokenMapsToCategorical(
                tokens, Source, tokEntityId, batchSz))
            {
                ct.ThrowIfCancellationRequested();
                yield return batch;
                await Task.Yield();
            }
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string configPath = Path.Combine(_modelDir, "config.json");
        if (!File.Exists(configPath)) return Task.FromResult<long?>(null);
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
        return Task.FromResult<long?>(distinctVocab + relations);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
