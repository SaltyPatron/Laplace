using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OMW;

public sealed class OMWDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.EpochBarrier;

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
        boot.AddRelationType("IS_TRANSLATION_OF");
        boot.AddRelationType("HAS_LANGUAGE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            LanguageNames.TryAdd(n, 0);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) yield break;

        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;
        long cap = options.MaxInputUnits;
        bool legacy = string.Equals(
            Environment.GetEnvironmentVariable("LAPLACE_OMW_LEGACY"),
            "1", StringComparison.Ordinal);

        if (legacy)
        {
            await foreach (var change in OMWGrammarIngest.IngestFilesAsync(
                wnsDir, options.Languages, batch, cap, OmwIngestPhase.Combined, ct))
            {
                if (!options.DryRun) yield return change;
            }
            yield break;
        }

        await foreach (var change in OMWGrammarIngest.IngestFilesAsync(
            wnsDir, options.Languages, batch, cap, OmwIngestPhase.Content, ct))
        {
            if (!options.DryRun) yield return change;
        }

        await foreach (var change in OMWGrammarIngest.IngestFilesAsync(
            wnsDir, options.Languages, batch, cap, OmwIngestPhase.Attestations, ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) return null;
        var files = new List<IngestFileSpec>();
        foreach (string tab in OMWTabFiles.EnumerateTabFiles(wnsDir, options.Languages)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string lang = OMWTabFiles.FileLang(tab);
            long n = EtlInventory.EstimateNewlineCount(tab, ct);
            files.Add(new(lang, tab, n));
        }
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return new IngestInventory("records", total, files);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
