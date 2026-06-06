namespace Laplace.SubstrateCRUD;

/// <summary>
/// Per-source per-entity representation kind — matches the substrate-canonical
/// PhysicalityType entities bootstrapped at install time Stage 2.
/// Stored as <c>smallint</c> in <c>physicalities.kind</c>.
/// </summary>
public enum PhysicalityType : short
{
    /// <summary>Decomposition-bearing view (trajectory populated).</summary>
    Content = 1,

    /// <summary>Used-as-constituent view; typically no trajectory.</summary>
    BuildingBlock = 2,

    /// <summary>Source-embedding-space input-direction view via Procrustes alignment
    /// (e.g. AI-model embed_tokens row).</summary>
    Projection = 3,

    /// <summary>Source-embedding-space output-direction view via Procrustes alignment
    /// (e.g. AI-model lm_head row). Coexists with kind=Projection on the same
    /// (entity, source) tuple under UNIQUE(entity_id, source_id, kind) when input
    /// and output embeddings are untied (TinyLlama has tie_word_embeddings=false).</summary>
    ProjectionOutput = 4,
}
