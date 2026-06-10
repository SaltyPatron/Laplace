namespace Laplace.SubstrateCRUD;

public enum PhysicalityType : short
{
    /// <summary>Default witness geometry for text and corpus content.</summary>
    Content = 1,

    /// <summary>Reserved — not yet emitted by production decomposers.</summary>
    BuildingBlock = 2,

    /// <summary>S3 morph placement (e.g. TokenS3Morph).</summary>
    Projection = 3,

    /// <summary>Reserved — not yet emitted by production decomposers.</summary>
    ProjectionOutput = 4,
}
