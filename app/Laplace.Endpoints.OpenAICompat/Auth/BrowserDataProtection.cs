using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Endpoints.OpenAICompat.Auth;

internal static class BrowserDataProtection
{
    internal static IServiceCollection AddBrowserDataProtection(
        this IServiceCollection services, string? keyDirectory, bool documentGeneration)
    {
        if (documentGeneration)
        {
            // Document generation starts framework services during compilation.
            // Its cookie schemas require a provider, but no serving key material.
            services.AddDataProtection().SetApplicationName("Laplace")
                .UseEphemeralDataProtectionProvider();
        }
        else if (!string.IsNullOrWhiteSpace(keyDirectory))
        {
            Directory.CreateDirectory(keyDirectory);
            services.AddDataProtection().SetApplicationName("Laplace")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        }
        return services;
    }
}
