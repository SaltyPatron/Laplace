using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    public Hash128 SourceId => Source;
    public string SourceName => "WiktionaryDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;
    // kaikki wiktextract records are fat JSON trees (senses, translations, etymology,
    // pronunciation) — tens of KB each. Sizing at the 512-byte default would stage ~20×
    // the memory per batch; the real shape keeps batches small and memory bounded.
    public int EstimatedBytesPerRecord => 12_000;





    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "HAS_ETYMOLOGY",
                "HAS_HYPERNYM", "HAS_HYPONYM", "IS_PART_OF", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
                "DERIVATIONALLY_RELATED", "RELATED_TO", "IS_COORDINATE_TERM_WITH",
                "HAS_USAGE_REGISTER", "HAS_PART", "HAS_VARIANT_OF", "TRANSCRIBES_AS",
                "IS_TRANSLATION_OF", "ETYMOLOGICALLY_DERIVED_FROM", "BORROWED_FROM",
                "INHERITED_FROM", "ETYMOLOGICALLY_RELATED_TO", "DERIVED_FROM",
                "FORM_OF", "HAS_FEATURE", "MANNER_OF"],
            readbackNames: VocabularyNames, ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? file = ResolveInput(context.EcosystemPath, options.Languages);
        if (file is null) yield break;

        var witness = new WiktionaryGrammarWitness(options);
        bool preFilter = WiktionaryJsonFilter.NeedsLanguagePreFilter(file, options.Languages);
        Func<ReadOnlySpan<byte>, bool>? acceptRow = preFilter && options.Languages is { IsActive: true } langs
            ? line => WiktionaryJsonFilter.MatchesLanguageFilter(line, langs)
            : null;

        // One 22GB file = one pipeline = one core on the sequential lane.
        // Record-parallel compose across the P-cores unless a unit cap asks
        // for the sequential, exactly-bounded path.
        if (WorkingSetMode.Enabled && options.MaxInputUnits <= 0)
        {
            var source = EtlManifest.Get("wiktionary");
            int workers = CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 8);
            await foreach (var change in StructuredGrammarIngest.IngestFileParallelAsync(
                file,
                source.Modality.GrammarId,
                source.SourceId,
                witness: witness,
                witnessWeight: 0.7,
                batchLabelPrefix: "wiktionary",
                workerCount: workers,
                acceptRow: acceptRow,
                recordFraming: source.Modality.RecordFraming,
                containmentReader: context.Reader,
                ct: ct))
            {
                if (!options.DryRun) yield return change;
            }
            yield break;
        }

        await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
            file,
            EtlManifest.Get("wiktionary"),
            witness: witness,
            batchSize: options.BatchSize > 1 ? options.BatchSize : 1024,
            witnessWeight: 0.7,
            batchLabelPrefix: "wiktionary",
            reportUnits: null,
            acceptRow: acceptRow,
            maxInputUnits: options.MaxInputUnits,

            containmentReader: null,
            ct: ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string? file = ResolveInput(context.EcosystemPath, options.Languages);
        if (file is null) return Task.FromResult<IngestInventory?>(null);


        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.SingleFile(
                "jsonl", file, options.MaxInputUnits, ct));

        return CountInventoryAsync(context.EcosystemPath, options.Languages, ct);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(
            context, DecomposerOptions.ForWitness(SourceName), ct).ConfigureAwait(false);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string? ResolveInput(string dir, LanguageFilter? langs)
    {



        if (langs?.IsActive == true)
        {
            string eng = Path.Combine(dir, "kaikki.org-dictionary-English.jsonl");
            if (File.Exists(eng))
            {
                Console.Error.WriteLine(
                    $"[WiktionaryDecomposer] Language filter active -> using English-only corpus '{eng}' " +
                    "(kaikki.org-dictionary-English.jsonl), NOT the full multilingual raw-wiktextract-data.jsonl.");
                return eng;
            }
        }
        foreach (var name in new[] { "raw-wiktextract-data.jsonl", "kaikki.org-dictionary-English.jsonl" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                Console.Error.WriteLine($"[WiktionaryDecomposer] Using input corpus '{p}'.");
                return p;
            }
        }
        if (Directory.Exists(dir))
            foreach (var p in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                Console.Error.WriteLine($"[WiktionaryDecomposer] Falling back to first *.jsonl in dir: '{p}'.");
                return p;
            }
        return null;
    }

    private static Task<IngestInventory?> CountInventoryAsync(
        string dir, LanguageFilter? langs, CancellationToken ct)
    {
        string? file = ResolveInput(dir, langs);
        if (file is null) return Task.FromResult<IngestInventory?>(null);
        long n = EtlInventory.EstimateNewlineCount(file, ct);
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("jsonl", n, [new IngestFileSpec(Path.GetFileName(file), file, n)]));
    }
}
