namespace Laplace.SubstrateCRUD;

/// <summary>
/// Compositional altitude in the Merkle DAG (0–4). Meaningful only when
/// type_id is compositional (Codepoint/Grapheme/Word/Sentence/Document).
/// Vocabulary rows (sources, relation types, synsets, etc.) use <see cref="Vocabulary"/> — tier is inert.
/// </summary>
public static class EntityTier
{
    public const byte Codepoint = 0;
    public const byte Grapheme  = 1;
    public const byte Word      = 2;
    public const byte Sentence  = 3;
    public const byte Document  = 4;

    /// <summary>Tier column value for non-compositional vocabulary entities. Interpret only via type_id.</summary>
    public const byte Vocabulary = 0;
}
