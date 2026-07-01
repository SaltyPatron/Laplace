namespace Laplace.Cli;

internal static class EtlWitnessRegistrations
{
    private static bool _done;

    public static void RegisterAll()
    {
        if (_done) return;
        _done = true;
        Laplace.Decomposers.OMW.OMWEtlRegistration.Register();
        Laplace.Decomposers.ConceptNet.ConceptNetEtlRegistration.Register();
        Laplace.Decomposers.Atomic2020.Atomic2020EtlRegistration.Register();
        Laplace.Decomposers.Wiktionary.WiktionaryEtlRegistration.Register();
    }
}
