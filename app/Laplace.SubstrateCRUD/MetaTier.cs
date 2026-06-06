namespace Laplace.SubstrateCRUD;

/// <summary>
/// Meta-band tiers for canonical-named vocabulary entities (kinds, types,
/// sources, classifiers, reference ids). Mirrors the schema-of-record
/// constants in extension/laplace_substrate/sql/10_bootstrap.sql.in:78-80
/// (META_TIER/KIND_TIER/TRUST_TIER) — keep both in lockstep.
///
/// The tier law: tiers 0..N are CONTENT composition depth only — 0 is the
/// Unicode codepoint anchor, 1 observed units, 2 n-grams, upward as content
/// composes. Canonical-named meta vocabulary never occupies content tiers.
/// </summary>
public static class MetaTier
{
    public const short Meta  = 250;
    public const short RelationType = 248;
    public const short Trust = 247;
}
