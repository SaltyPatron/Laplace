namespace Laplace.Endpoints.OpenAICompat;

internal sealed record AuditReportSpec(
    string Scope,
    bool IncludeEvidence,
    bool IncludeConsensus,
    bool IncludeConvergence,
    bool Academic);

internal sealed record VisualizationExportSpec(
    int Nodes,
    int Edges,
    bool IncludeGeometry,
    bool IncludeEvidence,
    bool Interactive);

internal sealed record RecipeWorkSpec(
    string Action,
    int ContentItems,
    bool Commercial,
    bool PrivateExport);

internal sealed record WorkEstimate(
    string ServiceId,
    long MeteredItems,
    long BillableUnits,
    long ItemsPerUnit,
    string UnitName);

internal interface IReportQuoteCalculator
{
    WorkEstimate EstimateAudit(AuditReportSpec spec);
    WorkEstimate EstimateVisualization(VisualizationExportSpec spec);
    WorkEstimate EstimateRecipe(RecipeWorkSpec spec);
}

internal sealed class ReportQuoteCalculator : IReportQuoteCalculator
{
    private const long AuditSectionsPerUnit = 1;
    private const long VisualItemsPerUnit = 100;
    private const long RecipeItemsPerUnit = 100;
    private const long RecipeExportItemsPerUnit = 1_000;

    public WorkEstimate EstimateAudit(AuditReportSpec spec)
    {
        var scopeFactor = spec.Scope.Trim().ToLowerInvariant() switch
        {
            "full" => 3,
            "source" => 2,
            "tenant" => 2,
            _ => 1
        };

        var sections = 1;
        if (spec.IncludeConsensus)
            sections += 2;
        if (spec.IncludeEvidence)
            sections += 4;
        if (spec.IncludeConvergence)
            sections += 2;
        sections *= scopeFactor;
        if (spec.Academic)
            sections *= 2;

        return new WorkEstimate(
            ServiceId: "audit.deep_report",
            MeteredItems: sections,
            BillableUnits: Math.Max(1L, sections / AuditSectionsPerUnit),
            ItemsPerUnit: AuditSectionsPerUnit,
            UnitName: "audit_unit");
    }

    public WorkEstimate EstimateVisualization(VisualizationExportSpec spec)
    {
        var nodes = Math.Max(1, spec.Nodes);
        var edges = Math.Max(0, spec.Edges);
        long items = nodes + edges;
        if (spec.IncludeGeometry)
            items += nodes;
        if (spec.IncludeEvidence)
            items += edges * 2L;
        if (spec.Interactive)
            items *= 2;

        return new WorkEstimate(
            ServiceId: "visualization.deep_export",
            MeteredItems: items,
            BillableUnits: Math.Max(1L, (items + VisualItemsPerUnit - 1) / VisualItemsPerUnit),
            ItemsPerUnit: VisualItemsPerUnit,
            UnitName: "visual_unit");
    }

    public WorkEstimate EstimateRecipe(RecipeWorkSpec spec)
    {
        var action = spec.Action.Trim().ToLowerInvariant();
        if (action is "publish")
            return Flat("recipe.publish", "recipe");
        if (action is "access" or "use")
            return Flat("recipe.access", "use");

        var items = Math.Max(1, spec.ContentItems);
        if (spec.Commercial)
            items *= 2;

        if (spec.PrivateExport || action is "export")
        {
            return new WorkEstimate(
                ServiceId: "recipe.export",
                MeteredItems: items,
                BillableUnits: Math.Max(1L, (items + RecipeExportItemsPerUnit - 1) / RecipeExportItemsPerUnit),
                ItemsPerUnit: RecipeExportItemsPerUnit,
                UnitName: "content_thousand");
        }

        return new WorkEstimate(
            ServiceId: "recipe.compile",
            MeteredItems: items,
            BillableUnits: Math.Max(1L, (items + RecipeItemsPerUnit - 1) / RecipeItemsPerUnit),
            ItemsPerUnit: RecipeItemsPerUnit,
            UnitName: "recipe_unit");
    }

    private static WorkEstimate Flat(string serviceId, string unitName) =>
        new(serviceId, 1, 1, 1, unitName);
}
