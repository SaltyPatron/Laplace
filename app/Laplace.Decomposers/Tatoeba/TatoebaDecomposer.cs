using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaDecomposer : DecomposerMultiFile<GrammarIngestRecord, TatoebaSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TatoebaSource.SourceId;
    public static readonly Hash128 TrustClass = TatoebaSource.TrustClass;

    internal static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    /// <summary>Links naming a sentence id absent from sentences.csv — reported, never grounded.</summary>
    internal static long UnresolvedLinks;

    private readonly TatoebaIdMap _ids = new();

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;

    private HashSet<long>? _allowedSentenceIds;

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    // Both files stream in parallel across the file-worker pool: the link lane resolves ids
    // through the map OnInitializedAsync already built, so it needs no ordering barrier and
    // keeps MonolithSegmenter's intra-file segmentation (a sequential file barrier would drop
    // this 2-file source from ~10 compose lanes to 1).
    protected override IMultiFileRecordStream<GrammarIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        _allowedSentenceIds = options.Languages?.IsActive == true ? new HashSet<long>() : null;

        string sentences = Path.Combine(ecosystemPath, "sentences.csv");
        string links = Path.Combine(ecosystemPath, "links.csv");
        var files = new List<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)>();

        if (File.Exists(sentences))
        {
            Func<ReadOnlySpan<byte>, bool>? acceptSent = options.Languages is { IsActive: true } langs
                ? line => TatoebaRowFilter.MatchesSentenceLanguageFilter(line, langs)
                : null;
            files.Add((sentences, "tatoeba/sent", acceptSent));
        }

        if (File.Exists(links))
        {
            Func<ReadOnlySpan<byte>, bool>? acceptLink = _allowedSentenceIds is not null
                ? line => TatoebaRowFilter.MatchesLinkFilter(line, _allowedSentenceIds)
                : null;
            files.Add((links, "tatoeba/link", acceptLink));
        }

        return new TatoebaMultiFileStream(files);
    }

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandlerForFile(string fileLabel)
    {
        var kind = fileLabel.EndsWith("/link", StringComparison.Ordinal)
            ? TatoebaRowKind.Link
            : TatoebaRowKind.Sentence;
        // The link lane resolves ids through the map OnInitializedAsync builds. If that never
        // ran, every link would resolve to nothing and the whole translation corpus would
        // vanish silently — the exact failure class IngestRunner already refuses to report as
        // success. Fail loudly instead.
        if (kind == TatoebaRowKind.Link && _ids.Count == 0)
            throw new InvalidOperationException(
                "Tatoeba link lane started with an empty id map: OnInitializedAsync did not run "
                + "or sentences.csv was missing. Every IS_TRANSLATION_OF would be dropped.");
        return new GrammarIngestHandler(
            Source, "tsv",
            new TatoebaGrammarWitness(kind, _allowedSentenceIds, _ids),
            contextId: null);
    }

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int batch = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Tatoeba, options);
        int commitEpoch = fileLabel.EndsWith("/link", StringComparison.Ordinal) ? 1 : 0;
        return IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.StructuredGrammar(
                Source, fileLabel, batch, options, reader, witnessWeight: 1.0,
                commitEpoch: commitEpoch, profile: IngestSourceProfile.Tatoeba),
            options);
    }

    /// <summary>
    /// Resolve every sentence id to its CONTENT ROOT before any lane runs, so links.csv can
    /// attest between the real sentences instead of minting a surrogate entity per id.
    ///
    /// This is CPU only — <see cref="ContentTierSpine.ResolveRoot"/> is the same pure
    /// leaf-to-trunk compose the sentence lane performs, with a native fast path and a memo;
    /// it touches no database. Doing it here rather than as an ingest PHASE keeps both files
    /// streaming in parallel across the file-worker pool (the alternative, a sequential
    /// file barrier, loses MonolithSegmenter's intra-file segmentation and would drop this
    /// source from ~10 compose lanes to 1).
    ///
    /// Parsing note: sentences.csv is a strict 3-column TSV with no quoting, so splitting on
    /// TAB yields byte-identical text spans to the grammar composer the witness sees — the
    /// roots computed here and staged there are the same ids by construction.
    /// </summary>
    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        string sentences = Path.Combine(context.EcosystemPath, "sentences.csv");
        if (!File.Exists(sentences)) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long unresolvable = 0;
        await Parallel.ForEachAsync(
            File.ReadLinesAsync(sentences, ct),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            (line, _) =>
            {
                int t1 = line.IndexOf('\t');
                if (t1 <= 0) return ValueTask.CompletedTask;
                int t2 = line.IndexOf('\t', t1 + 1);
                if (t2 < 0) return ValueTask.CompletedTask;
                if (!long.TryParse(line.AsSpan(0, t1), out long id)) return ValueTask.CompletedTask;
                var text = line.AsSpan(t2 + 1);
                if (text.IsEmpty) return ValueTask.CompletedTask;

                if (ContentTierSpine.ResolveRoot(text.ToString()) is { } root)
                    _ids.Set(id, root);
                else
                    Interlocked.Increment(ref unresolvable);
                return ValueTask.CompletedTask;
            });

        context.Logger?.LogInformation(
            "TATOEBA_ID_MAP resolved={Resolved:N0} unresolvable={Unresolvable:N0} elapsed_s={Elapsed:F1} "
            + "(sentence ids are ingest scaffolding — resolved to content roots here, never stored)",
            _ids.Count, unresolvable, sw.Elapsed.TotalSeconds);
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (options.MaxInputUnits > 0)
        {
            var paths = new List<string>();
            string sentences = Path.Combine(context.EcosystemPath, "sentences.csv");
            string links = Path.Combine(context.EcosystemPath, "links.csv");
            if (File.Exists(sentences)) paths.Add(sentences);
            if (File.Exists(links)) paths.Add(links);
            return IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct);
        }
        return await EtlInventory.TatoebaAsync(context.EcosystemPath, options.Languages, ct);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }
}
