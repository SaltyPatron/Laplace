using System.Security.Cryptography;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class BrowserDataProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "laplace-keyring-" + Guid.NewGuid().ToString("N"));

    public BrowserDataProtectionTests() => Directory.CreateDirectory(_root);

    private static IHost Host(string path, bool documentGeneration) => new HostBuilder()
        .ConfigureServices(services => services.AddBrowserDataProtection(path, documentGeneration))
        .Build();

    [Fact]
    public async Task DocumentGenerationStartsAndProtectsWithoutOpeningTheServingKeyPath()
    {
        var path = Path.Combine(_root, "serving-path");
        await File.WriteAllTextAsync(path, "serving-path-tripwire");
        using var first = Host(path, true);
        await first.StartAsync();
        var protector = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("browser-ticket");
        var protectedValue = protector.Protect("document-generation");
        Assert.Equal("document-generation", protector.Unprotect(protectedValue));
        using var second = Host(path, true);
        await second.StartAsync();
        var other = second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("browser-ticket");
        Assert.Throws<CryptographicException>(() => other.Unprotect(protectedValue));
        Assert.Equal("serving-path-tripwire", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
        await second.StopAsync();
        await first.StopAsync();
    }

    [Fact]
    public async Task ServingHostsRecoverProtectedTicketsFromTheDeclaredPersistentKeyDirectory()
    {
        var path = Path.Combine(_root, "serving-keys");
        string protectedValue;
        using (var first = Host(path, false))
        {
            await first.StartAsync();
            protectedValue = first.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("browser-ticket").Protect("persistent-ticket");
            await first.StopAsync();
        }
        Assert.NotEmpty(Directory.EnumerateFiles(path, "*.xml"));
        using var second = Host(path, false);
        await second.StartAsync();
        Assert.Equal("persistent-ticket", second.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("browser-ticket").Unprotect(protectedValue));
        await second.StopAsync();
    }

    [Fact]
    public void ServingHostDoesNotReplaceAnInvalidKeyDirectoryWithTemporaryKeys()
    {
        var path = Path.Combine(_root, "invalid-keys");
        File.WriteAllText(path, "existing-file");
        Assert.Throws<IOException>(() => Host(path, false));
        Assert.Equal("existing-file", File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
