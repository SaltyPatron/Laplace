using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : GrammarIngestDecomposer<WiktionarySource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = WiktionarySource.SourceId;
    public static readonly Hash128 TrustClass = WiktionarySource.TrustClass;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.AcademicCuratedUserInput;
    protected override string ModalityId => "json";
    protected override double WitnessWeight => 0.7;

    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => VocabularyNames;

    protected override IGrammarWitness CreateWitness(DecomposerOptions options) =>
        new WiktionaryGrammarWitness(options);

    protected override async IAsyncEnumerable<GrammarIngestRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? file = ResolveInput(ecosystemPath, options.Languages);
        if (file is null) yield break;

        var source = EtlManifest.Get("wiktionary");
        bool preFilter = WiktionaryJsonFilter.NeedsLanguagePreFilter(file, options.Languages);
        Func<ReadOnlySpan<byte>, bool>? acceptRow = preFilter && options.Languages is { IsActive: true } langs
            ? line => WiktionaryJsonFilter.MatchesLanguageFilter(line, langs)
            : null;

        var sized = IngestSizing.ResolveForSource(IngestSourceProfile.Wiktionary, null);
        int workers = sized.ComposeWorkers;

        if (workers > 1)
        {
            var parallel = new ParallelGrammarFileRecordStream(
                file, source.Modality.GrammarId, acceptRow, source.Modality.RecordFraming, workers, ct);
            await foreach (var record in parallel.RecordsAsync(ct))
                yield return record;
        }
        else
        {
            var stream = GrammarFileRecordStream.ForSource(file, source, acceptRow);
            await foreach (var record in stream.RecordsAsync(ct))
                yield return record;
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
