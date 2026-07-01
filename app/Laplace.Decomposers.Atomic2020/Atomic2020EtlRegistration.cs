using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Atomic2020;

public static class Atomic2020EtlRegistration
{
    [ModuleInitializer]
    internal static void Init() => NativeGrammarIngest.RegisterType<Atomic2020Decomposer>();

    public static void Register() =>
        EtlWitnessFactory.Register(
            "Atomic2020Decomposer",
            _ => new Atomic2020GrammarWitness());
}
