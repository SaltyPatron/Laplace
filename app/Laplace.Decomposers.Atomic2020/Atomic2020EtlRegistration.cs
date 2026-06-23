using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Atomic2020;

/// <summary>
/// Registers Atomic2020's existing <see cref="Atomic2020GrammarWitness"/> (head/rel/tail triples,
/// rel-name mapped through the source dictionary, "none" tail fallback) with the generic ETL engine.
/// The per-split context id (train/dev/test, derived from the file stem) is carried by the manifest
/// row's <c>ContextIdFromFile</c> so each triple is tagged with its split exactly as
/// <see cref="Atomic2020Decomposer"/> does.
/// </summary>
public static class Atomic2020EtlRegistration
{
    public static void Register() =>
        EtlWitnessFactory.Register(
            "Atomic2020Decomposer",
            _ => new Atomic2020GrammarWitness());
}
