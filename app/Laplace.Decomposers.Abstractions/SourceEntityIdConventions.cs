using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical BLAKE3-128 ID formulas for per-source external-reference entities.
/// Used wherever a decomposer needs a stable, cross-session-idempotent ID for
/// a source-specific record (WordNet synset offset, Tatoeba sentence ID, etc.)
/// that is NOT derived from text content alone — per ADR 0016.
/// </summary>
public static class SourceEntityIdConventions
{
    /// <summary>
    /// Canonical ID for a WordNet 3.0 synset. Formula:
    /// <c>BLAKE3("wordnet/synset/{pos}/{byteOffset}")</c> where pos is the
    /// one-character POS tag (n, v, a, r, s) and byteOffset is the
    /// data-file byte offset that uniquely identifies the synset.
    /// </summary>
    public static Hash128 WordNetSynset(long byteOffset, char pos) =>
        Hash128.OfCanonical($"wordnet/synset/{pos}/{byteOffset}");

    /// <summary>
    /// Canonical ID for a Tatoeba sentence. Formula:
    /// <c>BLAKE3("tatoeba/sentence/{sentenceId}")</c>.
    ///
    /// <para>Note: the content-addressed entity ID derived via TextDecomposer
    /// of the sentence text is the PREFERRED identity for the sentence as a
    /// substrate entity. This Tatoeba-specific ID is used only for
    /// HAS_EXTERNAL_ID attestations — the cross-source join key.</para>
    /// </summary>
    public static Hash128 TatoebaSentence(long sentenceId) =>
        Hash128.OfCanonical($"tatoeba/sentence/{sentenceId}");
}
