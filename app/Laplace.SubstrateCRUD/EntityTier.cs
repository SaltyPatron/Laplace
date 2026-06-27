namespace Laplace.SubstrateCRUD;






public static class EntityTier
{
    // Tier is a single axis: COMPOSITION DEPTH. Nothing else may live here.
    public const byte Codepoint = 0;
    public const byte Grapheme  = 1;
    public const byte Word      = 2;
    public const byte Sentence  = 3;
    public const byte Document  = 4;

    /// <summary>
    /// Abstract anchors (POS, morphology values, languages, trust classes, relation types, meta-types)
    /// are NOT a composition tier. Their *category* lives in <c>type_id</c> (their meta-type), never in
    /// the tier field — the previous <c>Vocabulary = 5</c> jammed a category into the depth axis. Their
    /// canonical name is a single token, so their composition depth is Word. Identity stays distinct
    /// from content words via <c>type_id</c> (the ids differ), so Word-tier never flattens an anchor
    /// into a literal text occurrence of its name.
    /// </summary>
    public const byte Vocabulary = Word;
}
