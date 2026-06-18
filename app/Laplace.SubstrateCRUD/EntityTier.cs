namespace Laplace.SubstrateCRUD;






public static class EntityTier
{
    public const byte Codepoint = 0;
    public const byte Grapheme  = 1;
    public const byte Word      = 2;
    public const byte Sentence  = 3;
    public const byte Document  = 4;

    /// <summary>
    /// Abstract vocabulary (POS, morphology values, languages, category anchors, relation types).
    /// Must not share tier 0 with codepoint atoms — tier 0 is perfcache/codepoint geometry only.
    /// </summary>
    public const byte Vocabulary = 5;
}
