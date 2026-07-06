using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Wiktionary;

public static class WiktionaryEtlRegistration
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnsureRegistered() => Register();

    public static void Register() =>
        EtlWitnessFactory.Register(
            "WiktionaryDecomposer",
            ctx => new WiktionaryGrammarWitness(ctx.Options),
            () => WiktionaryDecomposer.VocabularyNames.Keys.ToArray());
}
