using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : IDecomposer, IIngestInventoryProvider{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WiktionaryDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    // Unordered: rows are self-contained; the cross-entry relations (HAS_HYPERNYM, IS_SYNONYM_OF,
    // IS_TRANSLATION_OF, ETYMOLOGICALLY_DERIVED_FROM) name target lemmas by content-addressed id, so
    // forward/cross-entry references are legal and N workers can commit batches concurrently.

    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

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
        boot.AddRelationType("HAS_PART");
        boot.AddRelationType("HAS_VARIANT_OF");
        boot.AddRelationType("TRANSCRIBES_AS");
        boot.AddRelationType("IS_TRANSLATION_OF");
        boot.AddRelationType("ETYMOLOGICALLY_DERIVED_FROM");
        boot.AddRelationType("BORROWED_FROM");
        boot.AddRelationType("INHERITED_FROM");
        boot.AddRelationType("ETYMOLOGICALLY_RELATED_TO");
        boot.AddRelationType("DERIVED_FROM");
        boot.AddRelationType("FORM_OF");
        boot.AddRelationType("HAS_FEATURE");
        boot.AddRelationType("MANNER_OF");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            VocabularyNames.TryAdd(n, 0);
    }

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

        await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
            file,
            modalityId: "json",
            sourceId: Source,
            witness: witness,
            batchSize: options.BatchSize > 1 ? options.BatchSize : 1024,
            witnessWeight: 0.7,
            batchLabelPrefix: "wiktionary",
            reportUnits: null,
            acceptRow: acceptRow,
            ct: ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        
        
        if (options.Languages?.IsActive == true)
            return Task.FromResult<IngestInventory?>(null);
        return CountInventoryAsync(context.EcosystemPath, langs: null, ct);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        
        
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string? ResolveInput(string dir, LanguageFilter? langs)
    {
        // An active language filter swaps the full multilingual corpus (raw-wiktextract-data.jsonl)
        // for the English-only kaikki.org export. Log this explicitly so a filtered run is never
        // silently a different corpus than an unfiltered one.
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

    private static async Task<IngestInventory?> CountInventoryAsync(
        string dir, LanguageFilter? langs, CancellationToken ct)
    {
        string? file = ResolveInput(dir, langs);
        if (file is null) return null;
        long n = await EtlInventory.CountDataLinesAsync(file, static line =>
            line.Length > 0 && line[0] == '{', ct: ct);
        return new IngestInventory("jsonl", n, [new IngestFileSpec(Path.GetFileName(file), file, n)]);
    }
}
