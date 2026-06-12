using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WiktionaryDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_ETYMOLOGY");
        boot.AddRelationType("HAS_HYPERNYM");
        boot.AddRelationType("HAS_HYPONYM");
        boot.AddRelationType("IS_PART_OF");
        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("IS_ANTONYM_OF");
        boot.AddRelationType("DERIVATIONALLY_RELATED");
        boot.AddRelationType("RELATED_TO");
        boot.AddRelationType("IS_COORDINATE_TERM_WITH");
        boot.AddRelationType("HAS_USAGE_REGISTER");
        boot.AddRelationType("HAS_DOMAIN_TOPIC");
        boot.AddRelationType("ETYMOLOGICALLY_DERIVED_FROM");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? file = ResolveInput(context.EcosystemPath, options.Languages);
        if (file is null) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 1024;

        await foreach (var change in WiktionaryFastIngest.IngestJsonlAsync(file, batch, options, ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (options.Languages?.IsActive == true)
            return Task.FromResult<IngestInventory?>(null);
        return CountInventoryAsync(context.EcosystemPath, ct);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string? ResolveInput(string dir, LanguageFilter? langs)
    {
        if (langs?.IsActive == true)
        {
            string eng = Path.Combine(dir, "kaikki.org-dictionary-English.jsonl");
            if (File.Exists(eng)) return eng;
        }
        foreach (var name in new[] { "raw-wiktextract-data.jsonl", "kaikki.org-dictionary-English.jsonl" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        if (Directory.Exists(dir))
            foreach (var p in Directory.EnumerateFiles(dir, "*.jsonl")) return p;
        return null;
    }

    private static async Task<IngestInventory?> CountInventoryAsync(string dir, CancellationToken ct)
    {
        string? file = ResolveInput(dir, langs: null);
        if (file is null) return null;
        long n = await EtlInventory.CountDataLinesAsync(file, static line =>
            line.Length > 0 && line[0] == '{', ct: ct);
        return new IngestInventory("jsonl", n, [new IngestFileSpec(Path.GetFileName(file), file, n)]);
    }
}
