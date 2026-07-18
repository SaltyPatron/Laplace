using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

    // Wiktionary entries explode into many content trees (word, glosses, examples,
    // relations, forms, etymology) per record — size the working set like WordNet's
    // multi-emit line, not a single flat compose.
    public override int EstimatedComposeUnitsPerRecord => 6;

    internal static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => VocabularyNames;

    protected override void Compose(WiktionaryEntry record, SubstrateChangeBuilder builder) =>
        WiktionaryEmit.Emit(record, builder);

    protected override async IAsyncEnumerable<WiktionaryEntry> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? file = ResolveInput(ecosystemPath, options.Languages);
        if (file is null) yield break;

        LanguageFilter? langs = options.Languages;
        bool preFilter = WiktionaryJsonFilter.NeedsLanguagePreFilter(file, langs);

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            var span = lineMem.Span;
            if (span.IsEmpty) continue;

            // Byte-level language pre-filter for the multilingual raw corpus — drop
            // non-matching rows before parse (English-only corpus needs no pre-filter).
            if (preFilter && langs is { IsActive: true } active
                && !WiktionaryJsonFilter.MatchesLanguageFilter(span, active))
                continue;

            var entry = WiktionaryEntry.Parse(span, options);
            if (entry is not null)
                yield return entry;
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
        // Single-file valet (CLAUDE.md: multi-file sources accept <path> as a file, bare dir,
        // or corpus root — the same way `ingest ud <one.conllu>` works). A direct path to a
        // .jsonl file is used as-is. Without this the path was treated as a DIRECTORY and
        // Path.Combine(<file>, "kaikki...jsonl") resolved to nothing → input_total=0 noop.
        if (!string.IsNullOrEmpty(dir) && File.Exists(dir))
            return dir;

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
