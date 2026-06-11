using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// Drives the whole-model ETL against a REAL safetensors model, no database. Tallies entities and
// per-relation-type attestations, the FFN (COMPLETES_TO) edge density, wall-clock, and proves the
// transient-neuron invariant (zero neuron entities, zero token<->neuron half-relations). This is the
// modular FFN benchmark: point it at any model on the farm and read the numbers.
public static class ModelFfnBench
{
    // The Neuron entity type was purged with the cell archive; the raw hash stays so
    // the bench can keep asserting that no neuron entity ever materializes again.
    private static readonly Hash128 NeuronType = EntityTypeRegistry.Id("Neuron");

    private static readonly (string Name, Hash128 Id)[] RelTypes =
    {
        ("SIMILAR_TO",   RelationTypeRegistry.RelationTypeId("SIMILAR_TO")),
        ("ATTENDS",      RelationTypeRegistry.RelationTypeId("ATTENDS")),
        ("OV_RELATES",   RelationTypeRegistry.RelationTypeId("OV_RELATES")),
        ("COMPLETES_TO", RelationTypeRegistry.RelationTypeId("COMPLETES_TO")),
        ("DETECTS",      RelationTypeRegistry.RelationTypeId("DETECTS")),
        ("WRITES",       RelationTypeRegistry.RelationTypeId("WRITES")),
    };

    public static async Task<bool> RunAsync(string modelDir, ILogger log, CancellationToken ct = default)
    {
        string configPath    = Path.Combine(modelDir, "config.json");
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(configPath) || !File.Exists(tokenizerPath))
        {
            Console.Error.WriteLine($"model-bench: need config.json + tokenizer.json in {modelDir}");
            return false;
        }

        var recipe = LlamaRecipeExtractor.Parse(configPath);
        Console.WriteLine($"model-bench {modelDir}");
        Console.WriteLine($"  recipe : {recipe.NumLayers} layers, {recipe.NumHeads} heads / {recipe.NumKvHeads} kv, "
                          + $"d_model={recipe.HiddenSize}, interm={recipe.IntermediateSize}, vocab={recipe.VocabSize}");

        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        Console.WriteLine($"  tokens : {tokens.Count:N0} parsed");

        var (source, name) = ModelDecomposer.SourceForModel(modelDir);
        Console.WriteLine($"  source : {name}  ({source})");
        Console.WriteLine($"  theta  : {Environment.GetEnvironmentVariable("LAPLACE_MODEL_NOISE_SIGMA") ?? "5.0"} sigma / sqrt(dim)");
        Console.WriteLine();

        var etl = new ModelTableETL(modelDir, recipe, tokens, source,
            ModelDecomposer.ModelLayerTypeId, epochBase: 0, log);

        long entities = 0, neuronEntities = 0, attestations = 0;
        var byType = new Dictionary<Hash128, long>();
        foreach (var rt in RelTypes) byType[rt.Id] = 0;
        long otherType = 0;

        var sw = Stopwatch.StartNew();
        await foreach (var change in etl.EmitAsync(ct))
        {
            foreach (var e in change.Entities)
            {
                entities++;
                if (e.TypeId == NeuronType) neuronEntities++;
            }
            foreach (var a in change.Attestations)
            {
                attestations++;
                if (byType.TryGetValue(a.TypeId, out var c)) byType[a.TypeId] = c + 1;
                else otherType++;
            }
        }
        sw.Stop();

        long ffn = byType[RelTypes[3].Id];          // COMPLETES_TO
        long detects = byType[RelTypes[4].Id];
        long writes  = byType[RelTypes[5].Id];
        double secs = sw.Elapsed.TotalSeconds;
        int n = tokens.Count;
        double ffnPerLayer = recipe.NumLayers > 0 ? (double)ffn / recipe.NumLayers : 0;
        double ffnDensity  = (long)n * n > 0 ? (double)ffnPerLayer / ((double)n * n) : 0;

        Console.WriteLine();
        Console.WriteLine($"  ===== results ({secs:F1}s) =====");
        Console.WriteLine($"  entities total        : {entities,14:N0}");
        Console.WriteLine($"  attestations total    : {attestations,14:N0}  ({attestations / Math.Max(1e-9, secs):N0}/s)");
        foreach (var rt in RelTypes)
            Console.WriteLine($"    {rt.Name,-18}: {byType[rt.Id],14:N0}");
        if (otherType > 0)
            Console.WriteLine($"    {"(other)",-18}: {otherType,14:N0}");
        Console.WriteLine();
        Console.WriteLine($"  FFN COMPLETES_TO/layer : {ffnPerLayer,14:N0}  (density {ffnDensity:P3} of n^2, n={n:N0})");
        Console.WriteLine();

        bool ok = true;
        void Check(string label, bool cond)
        {
            Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {label}");
            ok &= cond;
        }
        Check("no neuron entities materialized", neuronEntities == 0);
        Check("no token<->neuron DETECTS half", detects == 0);
        Check("no neuron->token WRITES half", writes == 0);
        Check("FFN emitted token->token COMPLETES_TO", ffn > 0);
        Check("FFN density bounded (< 50% of n^2)", ffnDensity < 0.50);

        return ok;
    }
}
