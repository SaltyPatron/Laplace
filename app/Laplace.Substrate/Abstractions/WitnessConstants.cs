namespace Laplace.Decomposers.Abstractions;







public static class RelationTypeRank
{
    public const double Mandate = 1.00;
    public const double Definitional = 0.97;
    public const double Taxonomic = 0.90;
    public const double Equivalence = 0.82;
    public const double Partitive = 0.73;
    public const double Causal = 0.64;
    public const double Oppositional = 0.45;
    public const double Associative = 0.36;
    public const double TensorCalculation = 0.27;
    public const double LexicalGlue = 0.18;
    public const double ScalarValued = 0.12;
    public const double StandardsStructural = 0.08;
    public const double Probationary = 0.05;
}

/// <summary>
/// Witness priors read from the governed trust-class law (engine/manifest/trust_classes.toml).
/// The class is the only statement of a witness's trust; each name below is the prior of
/// the class it reads.
/// </summary>
public static class SourceTrust
{
    public static readonly double SubstrateMandate = ForClassName("SubstrateMandate");
    public static readonly double StandardsDerived = ForClassName("StandardsDerived");
    public static readonly double AcademicCurated = ForClassName("AcademicCurated");
    public static readonly double AcademicCuratedUserInput = ForClassName("AcademicCuratedWithUserInput");
    public static readonly double StructuredCorpus = ForClassName("StructuredCorpus");
    public static readonly double UserCuratedResource = ForClassName("UserCuratedResource");
    public static readonly double AiModelProbe = ForClassName("AIModelProbe");
    public static readonly double AppDerived = ForClassName("AppDerived");
    public static readonly double UserPrompt = ForClassName("UserPromptContent");
    public static readonly double Response = ForClassName("ResponseContent");
    public static readonly double Adversarial = ForClassName("AdversarialUntrusted");

    /// <summary>
    /// The witness prior of a governed trust class. Undeclared classes fail closed; source
    /// code never silently inherits a user/default prior.
    /// </summary>
    public static double ForClass(Laplace.Engine.Core.Hash128 trustClassId) =>
        Laplace.Engine.Core.TrustClassRegistry.Prior(trustClassId);

    /// <summary>The witness prior of a governed trust class, by its label.</summary>
    public static double ForClassName(string trustClass) =>
        Laplace.Engine.Core.TrustClassRegistry.Prior(trustClass);
}
