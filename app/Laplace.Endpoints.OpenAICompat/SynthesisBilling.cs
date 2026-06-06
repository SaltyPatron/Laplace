namespace Laplace.Endpoints.OpenAICompat;

// Dimensionality of a "build-a-model" synthesis recipe — the tensor mold the
// consensus substrate is poured into. These map 1:1 to the recipe.json fields
// consumed by `laplace synthesize substrate` (VocabSize / HiddenSize /
// NumHeads / NumKvHeads / IntermediateSize / NumLayers).
internal sealed record SynthesisRecipeDimensions(
    long VocabSize,
    long HiddenSize,
    int NumLayers,
    int NumHeads,
    int NumKvHeads,
    long IntermediateSize,
    bool TiedEmbeddings);

internal sealed record SynthesisEstimate(
    long Parameters,
    long BillableUnits,
    long ParametersPerUnit);

// Estimates the parameter count of a synthesis recipe and converts it to the
// billable meter (millions of parameters). This is the dimensionality-scaled
// price driver for build-a-model exports: a bigger mold (more vocab / hidden /
// heads / layers) yields more parameters and therefore more billable units.
//
// The accounting is a standard gated-decoder block estimate used solely for
// PRICING — it is not a load-bearing synthesis value and does not influence the
// actual re-export, which pours real consensus circuits into the recipe.
internal interface ISynthesisQuoteCalculator
{
    SynthesisEstimate Estimate(SynthesisRecipeDimensions dims);
}

internal sealed class SynthesisQuoteCalculator : ISynthesisQuoteCalculator
{
    // One billable unit per this many parameters (1 unit = 1M parameters).
    private const long ParametersPerUnitConst = 1_000_000;

    public SynthesisEstimate Estimate(SynthesisRecipeDimensions dims)
    {
        var hidden = Math.Max(1L, dims.HiddenSize);
        var heads = Math.Max(1, dims.NumHeads);
        var kvHeads = Math.Max(1, dims.NumKvHeads);
        var headDim = Math.Max(1L, hidden / heads);
        var intermediate = Math.Max(1L, dims.IntermediateSize);
        var layers = Math.Max(0, dims.NumLayers);
        var vocab = Math.Max(0L, dims.VocabSize);

        // Attention: Q (hidden x hidden) + K,V (hidden x kvHeads*headDim) + O (hidden x hidden).
        var qProj = hidden * hidden;
        var kvProj = 2L * hidden * (kvHeads * headDim);
        var oProj = hidden * hidden;
        var attnPerLayer = qProj + kvProj + oProj;

        // Gated FFN (SwiGLU): gate + up + down, each hidden x intermediate.
        var ffnPerLayer = 3L * hidden * intermediate;

        var perLayer = attnPerLayer + ffnPerLayer;
        var layerParams = perLayer * layers;

        // Embedding in + (untied) output projection.
        var embed = vocab * hidden;
        var embedTotal = dims.TiedEmbeddings ? embed : embed * 2L;

        var parameters = embedTotal + layerParams;
        var units = Math.Max(1L, (parameters + ParametersPerUnitConst - 1) / ParametersPerUnitConst);
        return new SynthesisEstimate(parameters, units, ParametersPerUnitConst);
    }
}
