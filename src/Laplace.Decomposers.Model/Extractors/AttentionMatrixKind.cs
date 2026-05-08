namespace Laplace.Decomposers.Model.Extractors;

/// <summary>
/// Discriminator for the four matrices that make up an attention head:
/// W_Q (query), W_K (key), W_V (value), W_O (output projection).
/// Resolves to a substrate concept entity hash via IConceptEntityResolver,
/// then participates in the operator-shape entity composition keyed by
/// (matrix_role, mechanistic_head_entity).
/// </summary>
public enum AttentionMatrixKind
{
    Query,
    Key,
    Value,
    Output,
}
