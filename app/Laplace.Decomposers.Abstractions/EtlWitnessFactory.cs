using System.Collections.Concurrent;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Context handed to a bespoke witness factory: the manifest row, the file currently being walked
/// (some sources derive per-file state from the path, e.g. OMW's file language), and the run options
/// (language scope, cross-language flag). The factory returns the source's existing witness so the
/// generic <see cref="EtlDecomposer"/> drives the real Transform through the one
/// <see cref="StructuredGrammarIngest"/> pipeline — the parity oracle.
/// </summary>
public readonly record struct EtlWitnessContext(EtlSource Source, string FilePath, DecomposerOptions Options);

/// <summary>
/// Registry mapping a manifest source name to the bespoke <see cref="IGrammarWitness"/> for sources
/// whose Transform genuinely exceeds declarative field-role mapping (JSON-tree walking, URI parsing,
/// per-row JSON meta, ILI lookup with language tracking). The source's own project registers its
/// existing witness here; <see cref="EtlWitness"/> then delegates to it, so the one generic
/// <see cref="EtlDecomposer"/> drives the real witness through the single
/// <see cref="StructuredGrammarIngest"/> pipeline — proving parity without reimplementing the
/// witness as data. Sources whose Transform IS pure field-role mapping carry an
/// <see cref="EtlSource.NodeEdgeMap"/> instead and need no entry here.
/// </summary>
public static class EtlWitnessFactory
{
    private static readonly ConcurrentDictionary<string, Func<EtlWitnessContext, IGrammarWitness>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    // Optional readback provider: a factory-routed witness tracks discovered names (languages, POS)
    // in its own source's static dict; this surfaces them to EtlDecomposer.CanonicalNamesForReadback
    // so the same canonicals get registered as the bespoke decomposer would register.
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
