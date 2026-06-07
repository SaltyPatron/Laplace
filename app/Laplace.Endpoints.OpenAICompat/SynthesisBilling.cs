namespace Laplace.Endpoints.OpenAICompat;

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

internal interface ISynthesisQuoteCalculator
{
    SynthesisEstimate Estimate(SynthesisRecipeDimensions dims);
}

internal sealed class SynthesisQuoteCalculator : ISynthesisQuoteCalculator
{
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

        var qProj = hidden * hidden;
        var kvProj = 2L * hidden * (kvHeads * headDim);
        var oProj = hidden * hidden;
        var attnPerLayer = qProj + kvProj + oProj;

        var ffnPerLayer = 3L * hidden * intermediate;

        var perLayer = attnPerLayer + ffnPerLayer;
        var layerParams = perLayer * layers;

        var embed = vocab * hidden;
        var embedTotal = dims.TiedEmbeddings ? embed : embed * 2L;

        var parameters = embedTotal + layerParams;
        var units = Math.Max(1L, (parameters + ParametersPerUnitConst - 1) / ParametersPerUnitConst);
        return new SynthesisEstimate(parameters, units, ParametersPerUnitConst);
    }
}
