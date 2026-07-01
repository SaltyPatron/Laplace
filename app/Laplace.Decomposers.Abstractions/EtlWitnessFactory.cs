using System.Collections.Concurrent;

namespace Laplace.Decomposers.Abstractions;

public readonly record struct EtlWitnessContext(EtlSource Source, string FilePath, DecomposerOptions Options);

public static class EtlWitnessFactory
{
    private static readonly ConcurrentDictionary<string, Func<EtlWitnessContext, IGrammarWitness>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Func<IReadOnlyCollection<string>>> _readback =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(
        string sourceName,
        Func<EtlWitnessContext, IGrammarWitness> factory,
        Func<IReadOnlyCollection<string>>? readback = null)
    {
        _factories[sourceName] = factory;
        if (readback is not null) _readback[sourceName] = readback;
    }

    public static bool IsRegistered(string sourceName) => _factories.ContainsKey(sourceName);

    internal static IGrammarWitness? TryCreate(in EtlWitnessContext ctx) =>
        _factories.TryGetValue(ctx.Source.Name, out var f) ? f(ctx) : null;

    public static IReadOnlyCollection<string> Readback(string sourceName) =>
        _readback.TryGetValue(sourceName, out var f) ? f() : Array.Empty<string>();
}
