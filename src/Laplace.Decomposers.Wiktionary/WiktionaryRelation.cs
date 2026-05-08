namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// One Wiktionary lexical relation reference (synonym / hypernym / hyponym /
/// meronym / holonym / coordinate_term). Wiktionary keeps these as
/// <c>{ "word": "...", "source": "...", "tags": [...] }</c> objects within
/// the parent entry's relation arrays.
/// </summary>
public sealed record WiktionaryRelation(string Word);
