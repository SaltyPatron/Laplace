namespace Laplace.Endpoints.OpenAICompat;

internal sealed record TraceReportRequest(
    int Depth,
    int Beam,
    bool Academic);

internal sealed record TraceEstimate(
    long TraceNodes,
    long BillableUnits,
    long NodesPerUnit);

internal interface ITraceQuoteCalculator
{
    TraceEstimate Estimate(TraceReportRequest request);
}

internal sealed class TraceQuoteCalculator : ITraceQuoteCalculator
{
    private const long NodesPerUnitConst = 100;

    private const long AcademicNodeMultiplier = 3;

    public TraceEstimate Estimate(TraceReportRequest request)
    {
        var depth = Math.Max(1, request.Depth);
        var beam = Math.Max(1, request.Beam);

        var nodes = (long)depth * beam;
        if (request.Academic)
            nodes *= AcademicNodeMultiplier;

        var units = Math.Max(1L, (nodes + NodesPerUnitConst - 1) / NodesPerUnitConst);
        return new TraceEstimate(nodes, units, NodesPerUnitConst);
    }
}
