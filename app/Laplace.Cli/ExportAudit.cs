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
        IEnumerable<string> Layers(string tpl) =>
            Enumerable.Range(0, recipe.NumLayers).Select(l => ArchitectureProfile.Layer(tpl, l));

        await using var ds = new NpgsqlDataSourceBuilder(connString).Build();

        Console.WriteLine();
        Console.WriteLine($"  {"role",-16} {"coverage",9} {"cos:law",8} {"rmse/M:law",11} {"cos:orig",9} {"rmse/M:orig",12} {"cos:fold",9} {"outliers>6M",12} {"survival",9}");

        async Task AuditRole(string role, Hash128 typeId, int rows, int cols, bool rowsAreOut,
            Func<Hash128, IReadOnlyList<int>?> inIdx, Func<Hash128, IReadOnlyList<int>?> outIdx,
            IReadOnlyList<string> instances)
        {
            double m = ConsensusReExport.MoldArenaScale(refMap, instances);
            var poured = await ConsensusReExport.ReadTableArenaAsync(ds, typeId, rows, cols, rowsAreOut, inIdx, outIdx, m);
            long total = poured.Cells.LongLength;

            long covered = 0;
            foreach (var c in poured.Cells) if (c != 0f) covered++;

            int n = instances.Count;
            var tm = new double[total];
            foreach (var name in instances)
            {
                var o = WeightTensorETL.LoadTensorF32(refMap, name, total);
                for (long i = 0; i < total; i++) tm[i] += Math.Tanh(o[i] / m);
            }
            double clampW = 6.0;
            for (long i = 0; i < total; i++)
            {
                double s = Math.Clamp(tm[i] / n, -(1.0 - 1e-12), 1.0 - 1e-12);
                tm[i] = Math.Clamp(MathAtanh(s), -clampW, clampW) * m;
            }

            double dotLaw = 0, nA = 0, nT = 0, seLaw = 0;
            for (long i = 0; i < total; i++)
            {
                double a = poured.Cells[i], b = tm[i];
                dotLaw += a * b; nA += a * a; nT += b * b;
                double d = a - b; seLaw += d * d;
            }
            double cosLaw = dotLaw / (Math.Sqrt(nA) * Math.Sqrt(nT) + 1e-30);
            double rmseLaw = Math.Sqrt(seLaw / total) / m;

            double cosOrigSum = 0, rmseOrigSum = 0, cosFoldSum = 0;
            long outCells = 0; double survSum = 0; long survN = 0;
            foreach (var name in instances)
            {
                var o = WeightTensorETL.LoadTensorF32(refMap, name, total);
                double dotO = 0, nO = 0, seO = 0, dotF = 0, nF = 0;
                for (long i = 0; i < total; i++)
                {
                    double a = poured.Cells[i], b = o[i], t = tm[i];
                    dotO += a * b; nO += b * b;
                    double d = a - b; seO += d * d;
                    dotF += t * b; nF += t * t;
                    if (Math.Abs(b) > clampW * m)
                    {
                        outCells++;
                        survSum += Math.Abs(a) / Math.Abs(b);
                        survN++;
                    }
                }
                cosOrigSum += dotO / (Math.Sqrt(nA) * Math.Sqrt(nO) + 1e-30);
                rmseOrigSum += Math.Sqrt(seO / total) / m;
                cosFoldSum += dotF / (Math.Sqrt(nF) * Math.Sqrt(nO) + 1e-30);
            }
            double survival = survN > 0 ? survSum / survN : 1.0;
            Console.WriteLine(
                $"  {role,-16} {100.0 * covered / total,8:F2}% {cosLaw,8:F4} {rmseLaw,11:F4} {cosOrigSum / n,9:F4} {rmseOrigSum / n,12:F4} {cosFoldSum / n,9:F4} {outCells,12:N0} {100.0 * survival,8:F1}%  rel={poured.Relations:N0} nz={covered:N0}/{total:N0}");
        }

        await AuditRole("EMBEDS", ModelDecomposer.EmbedsTypeId, vocab, dModel, false, tok, chan, [prof.EmbedTokens]);
        await AuditRole("OUTPUT_PROJECTS", ModelDecomposer.OutputProjectsTypeId, vocab, dModel, true, chan, tok, [prof.LmHead ?? prof.EmbedTokens]);
        await AuditRole("Q_PROJECTS", ModelDecomposer.QProjectsTypeId, attnOut, dModel, true, chan, attn, [.. Layers(prof.QProj)]);
        await AuditRole("K_PROJECTS", ModelDecomposer.KProjectsTypeId, kvDim, dModel, true, chan, kv, [.. Layers(prof.KProj)]);
        await AuditRole("V_PROJECTS", ModelDecomposer.VProjectsTypeId, kvDim, dModel, true, chan, kv, [.. Layers(prof.VProj)]);
        await AuditRole("O_PROJECTS", ModelDecomposer.OProjectsTypeId, dModel, attnOut, true, attn, chan, [.. Layers(prof.OProj)]);
        if (prof.GateProj is not null)
            await AuditRole("GATES", ModelDecomposer.GatesTypeId, interm, dModel, true, chan, neuron, [.. Layers(prof.GateProj)]);
        await AuditRole("UP_PROJECTS", ModelDecomposer.UpProjectsTypeId, interm, dModel, true, chan, neuron, [.. Layers(prof.UpProj)]);
        await AuditRole("DOWN_PROJECTS", ModelDecomposer.DownProjectsTypeId, dModel, interm, true, neuron, chan, [.. Layers(prof.DownProj)]);

        var normNames = Enumerable.Range(0, recipe.NumLayers)
            .SelectMany(l => prof.PerLayerNorms.Select(t => ArchitectureProfile.Layer(t, l)))
            .Append(prof.FinalNorm)
            .Where(refMap.ContainsKey)
            .ToList();
        if (normNames.Count > 0)
        {
            double mN = ConsensusReExport.MoldArenaScale(refMap, normNames);
            var normV = await ConsensusReExport.ReadNormVectorAsync(ds, ModelDecomposer.NormScalesTypeId, dModel, chan, mN);
            double cosSum = 0, rmseSum = 0;
            foreach (var name in normNames)
            {
                var o = WeightTensorETL.LoadTensorF32(refMap, name, dModel);
                double dot = 0, nO = 0, nP = 0, se = 0;
                for (int i = 0; i < dModel; i++)
                {
                    dot += normV[i] * o[i]; nO += o[i] * o[i]; nP += normV[i] * normV[i];
                    double d = normV[i] - o[i]; se += d * d;
                }
                cosSum += dot / (Math.Sqrt(nP) * Math.Sqrt(nO) + 1e-30);
                rmseSum += Math.Sqrt(se / dModel) / mN;
            }
            Console.WriteLine(
                $"  {"NORM_SCALES",-16} {"",9} {"",8} {"",11} {cosSum / normNames.Count,9:F4} {rmseSum / normNames.Count,12:F4} {"",9} {"",12} {"",9}  ({normNames.Count} norm tensors vs one folded vector)");
        }

        Console.WriteLine();
        Console.WriteLine("  cos:law / rmse/M:law  = poured vs the law's reachable image (tanh-mean clamped to 6M); deviation = coverage holes + collision averaging");
        Console.WriteLine("  cos:orig / rmse/M:orig = poured vs each original layer tensor (total reconstruction error)");
        Console.WriteLine("  cos:fold              = law image vs originals (pure depth-fold cost, pipeline excluded)");
        return 0;
    }

    private static double MathAtanh(double x) => 0.5 * Math.Log((1.0 + x) / (1.0 - x));
}
