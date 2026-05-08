namespace Laplace.Decomposers.Wiktionary;

using System.Collections.Generic;

/// <summary>
/// One Wiktionary entry as parsed from a kaikki.org dump
/// (<c>kaikki.org-dictionary-{Language}.jsonl</c> or
/// <c>raw-wiktextract-data.jsonl</c>). Entries are per-(word, lang, pos)
/// and may carry per-sense + cross-lingual translation + lexical relation
/// detail; this record type captures the load-bearing subset the F4
/// Wiktionary decomposer consumes for the lexical + translation slice.
/// Sense glosses, etymology, pronunciation, and inflections live on the
/// raw entry; their decomposers attach in follow-up units.
/// </summary>
public sealed record WiktionaryEntryRecord(
    string                                Word,
    string                                Language,
    string                                LanguageCode,
    string                                Pos,
    string                                EtymologyText,
    IReadOnlyList<string>                 Glosses,
    IReadOnlyList<string>                 Pronunciations,
    IReadOnlyList<string>                 Forms,
    IReadOnlyList<WiktionaryRelation>     Synonyms,
    IReadOnlyList<WiktionaryRelation>     Hypernyms,
    IReadOnlyList<WiktionaryRelation>     Hyponyms,
    IReadOnlyList<WiktionaryRelation>     Meronyms,
    IReadOnlyList<WiktionaryRelation>     Holonyms,
    IReadOnlyList<WiktionaryRelation>     CoordinateTerms,
    IReadOnlyList<WiktionaryTranslation>  Translations);
