using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Kaikki/wiktextract JSONL decomposer. The 10GB corpus is STRUCTURED DATA, so it
/// rides a native streaming data path — one <see cref="System.Text.Json.Utf8JsonReader"/>
/// pass per line (<see cref="WiktionaryEntry.Parse"/>) into the shared compose lane —
/// NOT the per-line tree-sitter grammar spine (that spine is for source code and cost
/// millions of managed↔native AST crossings per row). Emitted attestations are
/// identical to the former witness; only the parse changed. The grammar-witness
/// adapter (<see cref="WiktionaryGrammarWitness"/>) still exists for the spine
/// conformance suite and routes through the same <see cref="WiktionaryEmit"/>.
/// </summary>
public sealed class WiktionaryDecomposer
    : ComposeDecomposer<WiktionaryEntry, WiktionarySource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = WiktionarySource.SourceId;
    public static readonly Hash128 TrustClass = WiktionarySource.TrustClass;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.AcademicCuratedUserInput;

    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => VocabularyNames;

    // Grammar-witness / dry paths still need Compose; the bulk ingest lane overrides
    // CreateHandler so ContentTierSpine.BuildTree runs on the compose fan instead of
    // serial DrainInto (DirectComposeHandler put the entire Emit walk on one core).
    protected override void Compose(WiktionaryEntry record, SubstrateChangeBuilder builder) =>
        WiktionaryEmit.Emit(record, builder);

    protected override IIngestRecordHandler<WiktionaryEntry> CreateHandler() =>
        new WiktionaryComposeHandler();

    // PARSE POOL, not a serial loop. The former `await foreach … Parse(span)` put
    // 10.5M rows of full Utf8JsonReader object-graph construction on ONE core while
    // MonolithSegmenter's compose fan sat downstream waiting on it — the pre-seed
    // review measured the parse as the lane's binding stage on the multilingual
    // corpus. The pool itself lives in the spine (ParallelLineParse — the managed
    // sibling of ParallelGrammarFileRecordStream; decomposers hand-rolling channels
    // is gate-banned); this lane only supplies the per-line function: byte-level
    // language pre-filter, then WiktionaryEntry.Parse. Entry order is not
    // preserved; nothing downstream reads it — records dedup and descend by
    // content, and the segmenter partitions per record.
    protected override IAsyncEnumerable<WiktionaryEntry> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct)
    {
        string? file = ResolveInput(ecosystemPath, options.Languages);
        if (file is null) return AsyncEnumerable.Empty<WiktionaryEntry>();

        LanguageFilter? langs = options.Languages;
        bool preFilter = WiktionaryJsonFilter.NeedsLanguagePreFilter(file, langs);
        int workers = Math.Max(1, IngestSizing.ResolveForSource(IngestSourceProfile.Wiktionary).ComposeWorkers);

        return ParallelLineParse.RecordsAsync<WiktionaryEntry>(
            file,
            line =>
            {
                if (preFilter && langs is { IsActive: true } active
                    && !WiktionaryJsonFilter.MatchesLanguageFilter(line, active))
                    return null;
                return WiktionaryEntry.Parse(line, options);
            },
            workers, ct);
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

    private const string EnglishCorpusFile = "kaikki.org-dictionary-English.jsonl";

    private static readonly IngestSourceLayout Layout = new()
    {
        Files = [IngestFileMatch.Name("raw-wiktextract-data.jsonl"), IngestFileMatch.Name(EnglishCorpusFile)],
    };

    internal static string? ResolveInput(string dir, LanguageFilter? langs)
    {
        // Single-file valet (CLAUDE.md: multi-file sources accept <path> as a file, bare dir,
        // or corpus root — the same way `ingest ud <one.conllu>` works). A direct path to a
        // .jsonl file is used as-is. Without this the path was treated as a DIRECTORY and
        // Path.Combine(<file>, "kaikki...jsonl") resolved to nothing → input_total=0 noop.
        if (IngestInput.IsSingleFile(dir))
            return dir;

        if (langs?.IsActive == true)
        {
            string eng = Path.Combine(dir, EnglishCorpusFile);
            if (File.Exists(eng))
            {
                Console.Error.WriteLine(
                    $"[WiktionaryDecomposer] Language filter active -> using English-only corpus '{eng}' " +
                    "(kaikki.org-dictionary-English.jsonl), NOT the full multilingual raw-wiktextract-data.jsonl.");
                return eng;
            }
        }
        return IngestInput.FilesIn(dir, Layout).FirstOrDefault();
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
