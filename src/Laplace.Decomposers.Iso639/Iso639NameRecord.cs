namespace Laplace.Decomposers.Iso639;

/// <summary>
/// One row from <c>iso-639-3_Name_Index.tab</c>. A language can have many
/// alternate / inverted names; the decomposer (Track E / E5) emits each as
/// a substrate entity (composition of its codepoint LINESTRING) and a
/// "has_name" edge from the language entity. Phase 3 / Track E / E1.
/// </summary>
public sealed record Iso639NameRecord(
    string LanguageId,
    string PrintName,
    string InvertedName);
