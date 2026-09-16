using System.Runtime.CompilerServices;

namespace Laplace.Endpoints.OpenAICompat.Tests;

internal static class TestEnvironmentPin
{
    /// <summary>
    /// WebApplicationFactory hosts use a coherent, process-local development
    /// baseline before AppComposition selects its stores. Neither an inherited
    /// production auth mode nor deployed Stripe settings may turn the in-memory
    /// test host into an authenticated company deployment at startup. Likewise,
    /// an inherited Postgres billing setting must not let these hosts write test
    /// quotes, keys or usage into the live app.billing_* tables.
    /// Key/identity enforcement scenarios still select their own authentication
    /// options in ConfigureTestServices. BillingStoreContractTests constructs the
    /// Postgres implementations directly and retains its database coverage.
    /// </summary>
    [ModuleInitializer]
    internal static void PinBillingStoreToMemory()
    {
        Environment.SetEnvironmentVariable("LAPLACE_AUTH_MODE", "header");
        Environment.SetEnvironmentVariable("LAPLACE_BILLING_STORE", "memory");
    }
}
