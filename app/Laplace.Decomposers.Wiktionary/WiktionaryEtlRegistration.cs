using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Registers Wiktionary's existing <see cref="WiktionaryGrammarWitness"/> (JSON sense/etymology/form
/// tree walker) with the generic ETL engine, so the one <see cref="EtlDecomposer"/> drives the real
/// Transform through the single <see cref="StructuredGrammarIngest"/> pipeline.
/// </summary>
public static class WiktionaryEtlRegistration
{
    public static void Register() =>
        EtlWitnessFactory.Register(
            "WiktionaryDecomposer",
            ctx => new WiktionaryGrammarWitness(ctx.Options),
            () => WiktionaryDecomposer.VocabularyNames.Keys.ToArray());
}
