using System.Runtime.CompilerServices;

namespace Laplace.Endpoints.OpenAICompat.Tests;

internal static class TestEnvironmentPin
{
    /// <summary>
    /// Runs before any WebApplicationFactory host composes: pins header auth and the
    /// in-memory billing store, so an inherited auth mode or Postgres billing setting
    /// cannot make a test host enforce keys or write quotes, keys or usage into the live
    /// app.billing_* tables. Key-enforcement tests select their own authentication in
    /// ConfigureTestServices; BillingStoreContractTests constructs the Postgres stores
    /// directly.
    /// </summary>
    [ModuleInitializer]
    internal static void PinBillingStoreToMemory()
    {
        Environment.SetEnvironmentVariable("LAPLACE_AUTH_MODE", "header");
        Environment.SetEnvironmentVariable("LAPLACE_BILLING_STORE", "memory");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR")))
        {
            var tempLogDir = Path.Combine(
                Path.GetTempPath(),
                "laplace-openaicompat-tests-logs",
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(tempLogDir);
            Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR", tempLogDir);
        }
    }
}
