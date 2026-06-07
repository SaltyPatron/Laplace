using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;

namespace Laplace.Cli;

internal static class ExportAudit
{
    internal static async Task<int> RunAsync(string modelDir, string connString, string perfcacheBlob)
    {
        CodepointPerfcache.Load(perfcacheBlob);
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
        {
            Console.Error.WriteLine($"usage: laplace audit-export <model-dir>  (not found: {modelDir})");
            return 2;
        }
        string recipePath = Path.Combine(modelDir, "config.json");
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(recipePath) || !File.Exists(tokenizerPath))
        {
            Console.Error.WriteLine("audit-export needs config.json + tokenizer.json in the model dir");
            return 2;
        }

        var recipe = LlamaRecipeExtractor.Parse(recipePath);
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        int vocab = recipe.VocabSize, dModel = recipe.HiddenSize;
        int headDim = dModel / Math.Max(1, recipe.NumHeads);
        int attnOut = recipe.NumHeads * headDim, kvDim = recipe.NumKvHeads * headDim;
        int interm = recipe.IntermediateSize;

        var refs = SafetensorsContainerParser.ParseModel(modelDir);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;
        if (refMap.Count == 0)
        {
            Console.Error.WriteLine("audit-export needs the original safetensors alongside the recipe");
            return 2;
        }

        var tokenSlots = new Dictionary<Hash128, List<int>>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.TokenId < 0 || t.TokenId >= vocab) continue;
            if (!tokenSlots.TryGetValue(t.EntityId, out var slots))
                tokenSlots[t.EntityId] = slots = new List<int>(1);
            slots.Add(t.TokenId);
        }
        var (moldSource, moldSourceName) = ModelDecomposer.SourceForModel(modelDir);
        Console.WriteLine($"audit-export: {moldSourceName} ({moldSource})");
        Console.WriteLine($"  vocab={vocab} d={dModel} layers={recipe.NumLayers} attnOut={attnOut} kv={kvDim} interm={interm}");

        Dictionary<Hash128, int[]> AxisMap(string space, int dim)
        {
            var m = new Dictionary<Hash128, int[]>(dim);
            for (int i = 0; i < dim; i++)
                m[SourceEntityIdConventions.ModelAxisEntity(moldSource, space, i)] = [i];
            return m;
        }
        Func<Hash128, IReadOnlyList<int>?> Of(Dictionary<Hash128, int[]> m) =>
            e => m.TryGetValue(e, out var s) ? s : null;
        Func<Hash128, IReadOnlyList<int>?> tok = e => tokenSlots.TryGetValue(e, out var s) ? s : null;
        var chan = Of(AxisMap("channel", dModel));
        var attn = Of(AxisMap("attn_dim", attnOut));
        var kv = Of(AxisMap("kv_dim", kvDim));
        var neuron = Of(AxisMap("neuron", interm));

        var prof = ArchitectureProfile.For(recipe.ModelType);

        await using var ds = new NpgsqlDataSourceBuilder(connString).Build();

        Console.WriteLine();
        Console.WriteLine($"  {"slot",-28} {"coverage",9} {"cos:law",8} {"rmse/M:law",11} {"cos:orig",9} {"rmse/M:orig",12} {"outliers>6M",12} {"survival",9}");

        int SpaceDim(string space) => space switch
        {
            "TOKEN"    => vocab,
            "channel"  => dModel,
            "attn_dim" => attnOut,
            "kv_dim"   => kvDim,
            "neuron"   => interm,
            _ => throw new InvalidOperationException($"no dimension for space '{space}'"),
        };
        Func<Hash128, IReadOnlyList<int>?> SpaceIndex(string space) => space switch
        {
            "TOKEN"    => tok,
            "channel"  => chan,
            "attn_dim" => attn,
            "kv_dim"   => kv,
            "neuron"   => neuron,
            _ => throw new InvalidOperationException($"no index map for space '{space}'"),
        };

        async Task AuditTableSlot(ArenaSlot slot)
        {
            int inDim = SpaceDim(slot.InSpace), outDim = SpaceDim(slot.OutSpace);
            int rows = slot.RowsAreOut ? outDim : inDim;
            int cols = slot.RowsAreOut ? inDim : outDim;
            double m = ConsensusReExport.MoldArenaScale(refMap, [slot.TensorName]);
            var poured = await ConsensusReExport.ReadTableArenaAsync(
                ds, slot.KindId, rows, cols, slot.RowsAreOut,
                SpaceIndex(slot.InSpace), SpaceIndex(slot.OutSpace), m);
            long total = poured.Cells.LongLength;

            long covered = 0;
            foreach (var c in poured.Cells) if (c != 0f) covered++;

            double clampW = 6.0;
            var o = WeightTensorETL.LoadTensorF32(refMap, slot.TensorName, total);
            var tm = new double[total];
            for (long i = 0; i < total; i++)
            {
                double s = Math.Clamp(Math.Tanh(o[i] / m), -(1.0 - 1e-12), 1.0 - 1e-12);
                tm[i] = Math.Clamp(MathAtanh(s), -clampW, clampW) * m;
            }

            double dotLaw = 0, nA = 0, nT = 0, seLaw = 0;
            double dotO = 0, nO = 0, seO = 0;
            long outCells = 0; double survSum = 0; long survN = 0;
            for (long i = 0; i < total; i++)
            {
                double a = poured.Cells[i], b = o[i], t = tm[i];
                dotLaw += a * t; nA += a * a; nT += t * t;
                double dl = a - t; seLaw += dl * dl;
                dotO += a * b; nO += b * b;
                double d = a - b; seO += d * d;
                if (Math.Abs(b) > clampW * m)
                {
                    outCells++;
                    survSum += Math.Abs(a) / Math.Abs(b);
                    survN++;
                }
            }
            double cosLaw = dotLaw / (Math.Sqrt(nA) * Math.Sqrt(nT) + 1e-30);
            double rmseLaw = Math.Sqrt(seLaw / total) / m;
            double cosOrig = dotO / (Math.Sqrt(nA) * Math.Sqrt(nO) + 1e-30);
            double rmseOrig = Math.Sqrt(seO / total) / m;
            double survival = survN > 0 ? survSum / survN : 1.0;
            string label = slot.Layer >= 0 ? $"{slot.Role}/L{slot.Layer}" : slot.Role;
            Console.WriteLine(
                $"  {label,-28} {100.0 * covered / total,8:F2}% {cosLaw,8:F4} {rmseLaw,11:F4} {cosOrig,9:F4} {rmseOrig,12:F4} {outCells,12:N0} {100.0 * survival,8:F1}%  rel={poured.Relations:N0} nz={covered:N0}/{total:N0}");
        }

        async Task AuditNormSlot(ArenaSlot slot)
        {
            double m = ConsensusReExport.MoldArenaScale(refMap, [slot.TensorName]);
            var normV = await ConsensusReExport.ReadNormVectorAsync(ds, slot.KindId, dModel, SpaceIndex(slot.InSpace), m);
            var o = WeightTensorETL.LoadTensorF32(refMap, slot.TensorName, dModel);
            double dot = 0, nO = 0, nP = 0, se = 0;
            for (int i = 0; i < dModel; i++)
            {
                dot += normV[i] * o[i]; nO += o[i] * o[i]; nP += normV[i] * normV[i];
                double d = normV[i] - o[i]; se += d * d;
            }
            double cos = dot / (Math.Sqrt(nP) * Math.Sqrt(nO) + 1e-30);
            double rmse = Math.Sqrt(se / dModel) / m;
            string label = slot.Layer >= 0 ? $"{slot.Role}/L{slot.Layer}" : slot.Role;
            Console.WriteLine(
                $"  {label,-28} {"",9} {"",8} {"",11} {cos,9:F4} {rmse,12:F4} {"",12} {"",9}");
        }

        foreach (var slot in ModelArenaPlan.Slots(recipe, prof))
        {
            if (!refMap.ContainsKey(slot.TensorName)) continue;
            if (slot.IsNorm) await AuditNormSlot(slot);
            else await AuditTableSlot(slot);
        }

        Console.WriteLine();
        Console.WriteLine("  cos:law / rmse/M:law  = poured vs the law's reachable image (tanh clamped to 6M); deviation = coverage holes + collision averaging");
        Console.WriteLine("  cos:orig / rmse/M:orig = poured vs the slot's one original tensor (total reconstruction error)");
        return 0;
    }

    private static double MathAtanh(double x) => 0.5 * Math.Log((1.0 + x) / (1.0 - x));
}
