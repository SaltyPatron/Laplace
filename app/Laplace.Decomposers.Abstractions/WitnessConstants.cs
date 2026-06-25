namespace Laplace.Decomposers.Abstractions;

// SINGLE SOURCE OF TRUTH is engine/manifest/relation_types.toml [ranks]. These C# constants mirror it
// for the few attestations whose witness weight is computed managed-side (Unicode/ISO standards props,
// response/prompt content, dbpedia) rather than resolved natively, and they are the expected values in
// RelationTypeRegistryTests. They were STALE after the "semantic salience" recalibration: structural
// metadata sat at 0.91 (near-top) instead of the new 0.08 floor — over-weighting Unicode scaffolding
// ~11x — and taxonomic/equivalence had drifted. Realigned to the manifest bands here.
public static class RelationTypeRank
{
    public const double Mandate             = 1.00;
    public const double Definitional        = 0.97;
    public const double Taxonomic           = 0.90;
    public const double Equivalence         = 0.82;
    public const double Partitive           = 0.73;
    public const double Causal              = 0.64;
    public const double Oppositional        = 0.45;
    public const double Associative         = 0.36;
    public const double TensorCalculation   = 0.27;
    public const double LexicalGlue         = 0.18;
    public const double ScalarValued        = 0.12;
    public const double StandardsStructural = 0.08;
    public const double Probationary        = 0.05;
}

public static class SourceTrust
{
    public const double SubstrateMandate         = 1.00;
    public const double StandardsDerived         = 0.95;
    public const double AcademicCurated          = 0.85;
    public const double AcademicCuratedUserInput = 0.78;
    public const double StructuredCorpus         = 0.70;
    public const double UserCuratedResource      = 0.60;
    public const double AiModelProbe             = 0.50;
    public const double AppDerived               = 0.40;
    public const double UserPrompt               = 0.30;
    public const double Response                 = 0.20;   // the substrate's own generated output — low/probationary
    public const double Adversarial              = 0.00;
}
