namespace Laplace.Endpoints.OpenAICompat;

// A request for a step-by-step "how the answer formed" report. Maps to the
// substrate's `laplace.generate_tree(p_prompt, p_kind, p_depth, p_beam)`, which
// returns the token-by-token / path-by-path traversal with per-path mu and
// witness fan-in. Depth and beam drive the trace size (and therefore price);
// the academic tier adds evidence-provenance / citation expansion per node.
internal sealed record TraceReportRequest(
    int Depth,
    int Beam,
    bool Academic);

internal sealed record TraceEstimate(
    long TraceNodes,
    long BillableUnits,
    long NodesPerUnit);

// Estimates the size of an explainability trace report and converts it to the
// billable meter (trace nodes). The "academic" tier expands every node with its
// evidence provenance / citations, which multiplies the work per node.
//
// This accounting is for PRICING ONLY. It does not bound or alter the actual
// generate_tree traversal, which produces the real path-by-path report.
internal interface ITraceQuoteCalculator
{
    TraceEstimate Estimate(TraceReportRequest request);
}

internal sealed class TraceQuoteCalculator : ITraceQuoteCalculator
{
    // One billable unit per this many trace nodes.
    private const long NodesPerUnitConst = 100;

    // The academic tier expands each node with its provenance/citation set; this
    // is the per-node cost multiplier applied to the node estimate.
    private const long AcademicNodeMultiplier = 3;

    public TraceEstimate Estimate(TraceReportRequest request)
    {
        var depth = Math.Max(1, request.Depth);
        var beam = Math.Max(1, request.Beam);

        // The frontier carries up to `beam` live paths at each of `depth` steps,
        // so the report surfaces on the order of depth * beam path nodes. This is
        // a predictable linear meter rather than the worst-case beam^depth tree.
        var nodes = (long)depth * beam;
        if (request.Academic)
            nodes *= AcademicNodeMultiplier;

        var units = Math.Max(1L, (nodes + NodesPerUnitConst - 1) / NodesPerUnitConst);
        return new TraceEstimate(nodes, units, NodesPerUnitConst);
    }
}
