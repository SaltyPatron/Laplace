namespace Laplace.Decomposers.Iso639;

/// <summary>
/// One row from <c>iso-639-3.tab</c>. Phase 3 / Track E / E1.
/// Fields preserved verbatim — the decomposer (separate concern) projects
/// these into substrate entities + property edges.
/// </summary>
public sealed record Iso639LanguageRecord(
    string Id,           // ISO 639-3 three-letter primary key
    string? Part2b,      // ISO 639-2 bibliographic
    string? Part2t,      // ISO 639-2 terminologic
    string? Part1,       // ISO 639-1 two-letter (where defined)
    Iso639Scope Scope,
    Iso639LanguageType Type,
    string ReferenceName,
    string? Comment);

public enum Iso639Scope
{
    Individual,
    Macrolanguage,
    Special,
}

public enum Iso639LanguageType
{
    Ancient,
    Constructed,
    Extinct,
    Historical,
    Living,
    Special,
}
