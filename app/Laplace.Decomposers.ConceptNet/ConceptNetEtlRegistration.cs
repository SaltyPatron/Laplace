using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

/// <summary>
/// Registers ConceptNet's existing <see cref="ConceptNetGrammarWitness"/> with the generic ETL
/// engine. The witness itself re-checks the language filter per row (returns early on a miss), so
/// driving it through <see cref="EtlDecomposer"/> without the file-level acceptRow pre-skip yields
/// identical output — the pre-skip is a throughput optimization, not a correctness gate.
/// </summary>
public static class ConceptNetEtlRegistration
{
    public static void Register() =>
        EtlWitnessFactory.Register(
            "ConceptNetDecomposer",
            ctx => new ConceptNetGrammarWitness(ctx.Options.Languages),
            () => ConceptNetDecomposer.LanguageNames.Keys.ToArray());
}
