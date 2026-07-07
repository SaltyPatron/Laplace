using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : DecomposerOrchestrator, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    public override Hash128 SourceId => Source;
    public override string SourceName => "WiktionaryDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    // kaikki wiktextract records are fat JSON trees (senses, translations, etymology,
    // pronunciation) — tens of KB each. Sizing at the 512-byte default would stage ~20×
    // the memory per batch; the real shape keeps batches small and memory bounded.
    public int EstimatedBytesPerRecord => IngestSourceProfile.Wiktionary.EstBytesPerRecord;





    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "HAS_ETYMOLOGY",
                "HAS_HYPERNYM", "HAS_HYPONYM", "IS_PART_OF", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
                "DERIVATIONALLY_RELATED", "RELATED_TO", "IS_COORDINATE_TERM_WITH",
                "HAS_USAGE_REGISTER", "HAS_PART", "HAS_VARIANT_OF", "TRANSCRIBES_AS",
                "IS_TRANSLATION_OF", "ETYMOLOGICALLY_DERIVED_FROM", "BORROWED_FROM",
                "INHERITED_FROM", "ETYMOLOGICALLY_RELATED_TO", "DERIVED_FROM",
                "FORM_OF", "HAS_FEATURE", "MANNER_OF"],
            readbackNames: VocabularyNames, ct: ct);

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
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

        var source = EtlManifest.Get("wiktionary");
        int workers = CpuTopology.ResolveCpuBoundWorkers(headroom: 1);
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

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(
            context, DecomposerOptions.ForWitness(SourceName), ct).ConfigureAwait(false);
        return inv?.TotalInputUnits;
    }

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
            if (File.Exists(p)) return p;
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
