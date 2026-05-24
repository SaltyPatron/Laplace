namespace Laplace.SubstrateCRUD;

/// <summary>
/// Per-source per-entity representation kind — matches the substrate-canonical
/// PhysicalityKind entities bootstrapped at install time per ADR 0042 Stage 2.
/// Stored as <c>smallint</c> in <c>physicalities.kind</c>.
/// </summary>
public enum PhysicalityKind : short
{
    /// <summary>Decomposition-bearing view (trajectory populated).</summary>
    Content = 1,

    /// <summary>Used-as-constituent view; typically no trajectory.</summary>
    BuildingBlock = 2,

    /// <summary>Source-embedding-space view via Procrustes alignment.</summary>
    Projection = 3,
}
