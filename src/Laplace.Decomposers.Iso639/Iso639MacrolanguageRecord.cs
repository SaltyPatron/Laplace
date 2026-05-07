namespace Laplace.Decomposers.Iso639;

/// <summary>
/// One row from <c>iso-639-3-macrolanguages.tab</c>. M_Id is the macrolanguage
/// (e.g., "ara" for Arabic); I_Id is an individual member (e.g., "arb"
/// Standard Arabic, "ary" Moroccan Arabic). Status is "A" (Active) or "R"
/// (Retired). Phase 3 / Track E / E1.
/// </summary>
public sealed record Iso639MacrolanguageRecord(
    string MacrolanguageId,
    string IndividualId,
    Iso639MacrolanguageStatus Status);

public enum Iso639MacrolanguageStatus
{
    Active,
    Retired,
}
