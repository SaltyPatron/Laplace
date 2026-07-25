using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Laplace.Cli.Spectre;

/// <summary>
/// Bridges Spectre.Console.Cli command construction to Microsoft.Extensions.DependencyInjection,
/// so a command can constructor-inject the shared services (ILoggerFactory / ILogger, the seed
/// decomposer resolver, DB access) instead of reaching into a static locator. GH #603.
/// </summary>
public sealed class DiTypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;
    public DiTypeRegistrar(IServiceCollection services) => _services = services;

    public void Register(Type service, Type implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
        => _services.AddSingleton(service, _ => factory());

    public ITypeResolver Build() => new DiTypeResolver(_services.BuildServiceProvider());
}

/// <summary>Resolves Spectre command instances (and their dependencies) from the built provider.</summary>
public sealed class DiTypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;
    public DiTypeResolver(IServiceProvider provider) => _provider = provider;

    // Spectre asks for command types it registered plus their ctor dependencies. Unregistered
    // types (a command with no explicit registration) resolve via the provider's activator.
    public object? Resolve(Type? type)
        => type is null ? null
           : _provider.GetService(type) ?? ActivatorUtilities.CreateInstance(_provider, type);

    public void Dispose()
    {
        if (_provider is IDisposable d) d.Dispose();
    }
}
