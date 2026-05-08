namespace Laplace.Decomposers.Omw;

/// <summary>
/// One row from an OMW (Open Multilingual WordNet) wn-data-{lang}.tab
/// file. Format per OMW convention:
///   synset_id [tab] type [tab] value
/// where type is "lemma" (the lemma string in this language) or "def"
/// (gloss/definition) or "exe" (example sentence). synset_id is the
/// canonical Princeton WordNet synset identifier (e.g., "00001740-n"
/// for the noun synset offset 1740).
/// </summary>
public sealed record OmwTabRecord(string SynsetId, string Type, string Value);
