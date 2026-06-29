using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OMW;

public sealed class OMWDecomposer : IDecomposer, IIngestInventoryProvider{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");


    public Hash128 SourceId     => Source;
    public string  SourceName   => "OMWDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    internal static void TrackLanguage(string? langInput) =>
        VocabularyNames.TrackLanguage(LanguageNames, langInput);

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("HAS_LANGUAGE");
        boot.AddRelationType("HAS_POS");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            LanguageNames.TryAdd(n, 0);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) yield break;

        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;
        long cap = options.MaxInputUnits;

        await foreach (var change in OMWGrammarIngest.IngestFilesAsync(
            wnsDir, options.Languages, batch, cap, context.Reader, ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) return Task.FromResult<IngestInventory?>(null);
        var paths = OMWTabFiles.EnumerateTabFiles(wnsDir, options.Languages)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
