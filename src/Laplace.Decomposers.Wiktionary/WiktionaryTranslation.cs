namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// One Wiktionary translation reference. Provides a direct word-to-word
/// cross-language edge (distinct from OMW's word-to-synset edges) anchored
/// on the source entry's word. Romanization and per-sense disambiguation
/// are kept on the raw entry; this record carries the load-bearing fields
/// used by the F4 lexical+translation decomposer slice.
/// </summary>
public sealed record WiktionaryTranslation(
    string  TargetLanguageCode,
    string  TargetWord);
