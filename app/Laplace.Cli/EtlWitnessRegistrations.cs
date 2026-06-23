namespace Laplace.Cli;

/// <summary>
/// One-time registration of the already-grammar-conforming sources' bespoke witnesses with the
/// generic ETL engine (<c>EtlWitnessFactory</c>). The CLI is the composition root that knows the
/// concrete source projects; the engine in Laplace.Decomposers.Abstractions stays free of any
/// dependency on them. Idempotent.
/// </summary>
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
