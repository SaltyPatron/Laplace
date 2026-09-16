using System.Runtime.CompilerServices;

namespace Laplace.Endpoints.OpenAICompat.Tests;

internal static class TestEnvironmentPin
{
    /// <summary>
    /// WebApplicationFactory contracts explicitly use header-mode development
    /// and in-memory billing, independently of the runner's installed identity
    /// and payment configuration. Per-factory PostConfigure auth overrides still
    /// exercise key/identity enforcement. Database contracts construct the real
    /// Postgres stores directly and run in the disposable database proof.
    /// </summary>
    [ModuleInitializer]
    internal static void PinBillingStoreToMemory()
    {
        Environment.SetEnvironmentVariable("LAPLACE_AUTH_MODE", "header");
        Environment.SetEnvironmentVariable("LAPLACE_BILLING_STORE", "memory");
    }
}
