using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

public static class ConceptNetEtlRegistration
{
    [ModuleInitializer]
    internal static void Init() => NativeGrammarIngest.RegisterType<ConceptNetDecomposer>();

    public static void Register() =>
        EtlWitnessFactory.Register(
            "ConceptNetDecomposer",
            ctx => new ConceptNetGrammarWitness(ctx.Options.Languages),
            () => ConceptNetDecomposer.LanguageNames.Keys.ToArray());
}
