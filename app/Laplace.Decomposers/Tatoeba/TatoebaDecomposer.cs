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
        long unresolvable = 0, seen = 0, nextReport = ReportEvery;

        // Read on ONE thread into fixed batches, resolve each batch across all cores.
        //
        // The first cut of this used Parallel.ForEachAsync over File.ReadLinesAsync and was
        // effectively SERIAL: an async line enumerator is a single cursor, so the workers
        // spent their lives awaiting MoveNextAsync. MEASURED on the live box at 135% CPU of
        // 1200% available, 13 minutes in, with no output at all — a silent prelude that is
        // indistinguishable from a hang, which is exactly how it was reported. Batching
        // moves the parallel boundary off the cursor and onto the work.
        var batch = new List<string>(BatchLines);
        foreach (var line in File.ReadLines(sentences))
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(line);
            if (batch.Count < BatchLines) continue;
            ResolveBatch(batch, ref unresolvable);
            seen += batch.Count;
            batch.Clear();
            if (seen < nextReport) continue;
            nextReport = seen + ReportEvery;
            context.Logger?.LogInformation(
                "TATOEBA_ID_MAP building: lines={Seen:N0} resolved={Resolved:N0} elapsed_s={Elapsed:F0}",
                seen, _ids.Count, sw.Elapsed.TotalSeconds);
        }
        if (batch.Count > 0) { ResolveBatch(batch, ref unresolvable); seen += batch.Count; }

        context.Logger?.LogInformation(
            "TATOEBA_ID_MAP resolved={Resolved:N0} of {Seen:N0} unresolvable={Unresolvable:N0} "
            + "elapsed_s={Elapsed:F1} (sentence ids are ingest scaffolding — resolved to content "
            + "roots here, never stored)",
            _ids.Count, seen, unresolvable, sw.Elapsed.TotalSeconds);
        await Task.CompletedTask;
    }

    /// <summary>Lines per parallel batch — big enough to amortise the fan-out, small enough
    /// that the reader is never far ahead of the resolvers.</summary>
    private const int BatchLines = 65_536;

    /// <summary>Progress cadence. A build this long must never be silent again.</summary>
    private const long ReportEvery = 2_000_000;

    private void ResolveBatch(List<string> lines, ref long unresolvable)
    {
        long bad = 0;
        Parallel.For(0, lines.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var line = lines[i].AsSpan();
                int t1 = line.IndexOf('\t');
                if (t1 <= 0) return;
                int t2 = line[(t1 + 1)..].IndexOf('\t');
                if (t2 < 0) return;
                t2 += t1 + 1;
                if (!long.TryParse(line[..t1], out long id)) return;
                var text = line[(t2 + 1)..];
                if (text.IsEmpty) return;

                if (ContentTierSpine.ResolveRoot(text.ToString()) is { } root) _ids.Set(id, root);
                else Interlocked.Increment(ref bad);
            });
        unresolvable += bad;
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
