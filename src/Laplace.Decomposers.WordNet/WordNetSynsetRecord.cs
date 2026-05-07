namespace Laplace.Decomposers.WordNet;

using System.Collections.Generic;

/// <summary>
/// One Princeton WordNet 3.0/3.1 synset, parsed from data.{noun,verb,adj,adv}.
/// Format per <c>wndb(5)</c>:
/// <code>
///   synset_offset lex_filenum ss_type w_cnt word lex_id [word lex_id ...] p_cnt [ptr ...] [frames ...] | gloss
/// </code>
/// Pointers (hypernym, hyponym, meronym, etc.) are captured for the decomposer
/// to emit as substrate edges between synset entities.
///
/// Per substrate invariant 1: synsets are content-addressed entities. Their
/// hash derives from constituent lemma hashes via Merkle composition; cross-
/// language synonymy emerges through OMW edges, not anchor mappings.
/// </summary>
public sealed record WordNetSynsetRecord(
    long                              SynsetOffset,
    int                               LexFileNumber,
    WordNetSynsetType                 Type,
    IReadOnlyList<WordNetLemma>       Lemmas,
    IReadOnlyList<WordNetPointer>     Pointers,
    string                            Gloss);

public enum WordNetSynsetType
{
    Noun,            // n
    Verb,            // v
    Adjective,       // a
    AdjectiveSatellite, // s
    Adverb,          // r
}

public sealed record WordNetLemma(string SurfaceForm, int LexId);

public sealed record WordNetPointer(
    string                  Symbol,         // !, @, ~, #m, #s, #p, etc.
    long                    TargetOffset,
    WordNetSynsetType       TargetType,
    int                     SourceLemmaIndex,
    int                     TargetLemmaIndex);
