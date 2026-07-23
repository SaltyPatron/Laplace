using System.Runtime.CompilerServices;

namespace Laplace.Endpoints.OpenAICompat.Tests;

internal static class TestEnvironmentPin
{
    /// <summary>
    /// Billing store resolution is auto (Postgres-preferred) in the app; tests must
    /// stay on the in-memory stores so WebApplicationFactory runs never write
    /// quotes/keys/usage into the live app.billing_* tables. The Postgres store
    /// contract is covered explicitly by BillingStoreContractTests, which constructs
    /// the Postgres implementations directly.
    /// </summary>
    [ModuleInitializer]
    internal static void PinBillingStoreToMemory()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_BILLING_STORE")))
            Environment.SetEnvironmentVariable("LAPLACE_BILLING_STORE", "memory");
    }
}
