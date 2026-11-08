using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.CILI;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.WordNet;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Dynamics;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    /// <summary>
    /// Line-delimited formats, where mean line length is the record size. Other formats
    /// (XML, Parquet, ...) keep the declared EstBytesPerRecord.
    /// </summary>
    private static readonly string[] LineDelimitedExtensions =
        [".jsonl", ".ndjson", ".csv", ".tsv", ".tab", ".conllu", ".txt"];

    private static IngestSourceProfile ApplyMeasuredRecordSize(
        IngestSourceProfile profile, string? path, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return profile;
        string ext = Path.GetExtension(path);
        if (!LineDelimitedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return profile;

        int measured = IngestSizing.MeasureBytesPerRecord(
            path, fallback: profile.EstBytesPerRecord);
        if (measured == profile.EstBytesPerRecord) return profile;

        Console.Error.WriteLine(
            $"ingest_record_size: source={sourceName} file={Path.GetFileName(path)} "
            + $"declared={profile.EstBytesPerRecord} measured={measured} "
            + $"ratio={(double)profile.EstBytesPerRecord / measured:F2}x — sizing from the file");
        return profile with { EstBytesPerRecord = measured };
    }

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath,
        IngestCliArgs? cli = null, bool skipSourceCompletion = false,
        IngestSourceProfile? sizingProfile = null)
    {
        IngestTopology.EnsureReady();
        long lastMs = -10_000;
        int progressIntervalMs = Laplace.Decomposers.Abstractions.IngestConsoleMode.ProgressMinIntervalMs;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastMs < progressIntervalMs) return;
            lastMs = now;
            double secs = Math.Max(0.001, p.Elapsed.TotalSeconds);
            long rowsNew = p.EntitiesInserted + p.PhysicalitiesInserted + p.AttestationsInserted;
            long inputProgress = Math.Max(p.InputUnitsDone, p.InputUnitsComposed);
            string filePart = p.FilesTotal > 0 ? $"files={p.FilesDone}/{p.FilesTotal} file_pct={p.FilePercent:F1}" : "";
            string inputPart = p.InputUnitsTotal > 0
                ? $"input={inputProgress}/{p.InputUnitsTotal} input_pct={p.InputPercent:F1}"
                  + (p.InputUnitsComposed > p.InputUnitsDone
                      ? $" composed={p.InputUnitsComposed:N0} committed={p.InputUnitsDone:N0}"
                      : "")
                : $"intents={p.UnitsApplied}/{p.UnitsProduced} intent_pct={p.InputPercent:F1}";
            string cur = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" current={p.CurrentFile}";
            Console.Error.WriteLine(
                $"INGEST_PROGRESS source={p.SourceName} layer={p.LayerOrder} unit_type={p.UnitType} "
                + $"{inputPart} {filePart}{cur} "
                + $"rows_new={rowsNew:N0} rate_input_s={inputProgress / secs:N0} rate_rows_new_s={rowsNew / secs:N0} "
                + $"round_trips={p.RoundTrips:N0} elapsed_s={p.Elapsed.TotalSeconds:F0}"
                + (p.UnitsFailed > 0 ? $" failed={p.UnitsFailed:N0} status=failed" : " status=running"));
        });
        // LAPLACE_INGEST_MAX_UNITS caps input volume (an operator scope, like --langs).
        // Batch/commit sizing comes from IngestSizing/MemoryTopology only.
        long maxUnits =
            long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_INGEST_MAX_UNITS"),
                out var mu) && mu > 0 ? mu : 0;
        var profile = sizingProfile ?? IngestSourceProfile.Default;
        // EstBytesPerRecord is the denominator of the per-worker memory budget. For a
        // line-delimited input file it is measured from that file, since one source can
        // admit files with very different record sizes; otherwise the declared profile stands.
        profile = ApplyMeasuredRecordSize(profile, ecosystemPath, sourceName);
        var sized = IngestSizing.ResolveForSource(profile);
        sized.Log(sourceName);
        int batch = sized.RecordBatchSize;
        int commitRows = sized.CommitRows;
        var decoOpts = DecomposerOptions.ForWitness(
            sourceName, batch, cli?.LangOverride, cli?.EmitCrossLanguageLinks);
        if (cli?.Force ?? false)
            decoOpts = decoOpts with { ReObservePresent = true };
        if (maxUnits > 0)
            decoOpts = decoOpts with { MaxInputUnits = maxUnits };
        return IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = skipLayerCheck,
            // Suppressing completion and bypassing its pre-run guard are separate:
            // path-parameterized sources suppress the marker, while --force only bypasses
            // the guard and still records terminal layer completion.
            SkipSourceCompletion = skipSourceCompletion,
            BypassSourceCompletionGuard = cli?.Force ?? false,
            EcosystemPath = ecosystemPath,
            BatchSize = batch,
            DecomposerOptions = decoOpts,
            CommitRows = commitRows,
            Progress = progress,
            RetryPolicy = TransientErrorRetryPolicy.Default,
            AbortOnTransientExhaustion = true,
        };
    }

    internal static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string? ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        // The codepoint perfcache is a read-only map over admitted Tier-0; content
        // witnessing composes through it, so it is loaded for every source, including
        // Unicode, whose admission still writes the Tier-0 rows from UCD.
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();
        var topo = IngestTopology.EnsureReady();

        NativeCorpusRuntime? corpusRuntime = dec is RepoDecomposer { VerifiedRepository: not null }
            ? ObserveCorpusRuntime() : null;
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var loggerFactory = CliRuntime.LoggerFactory;
        bool force = cli?.Force ?? false;
        var innerWriter = new NpgsqlSubstrateWriter(ds,
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());
        bool persistEvidence = ResolvePersistEvidence(cli);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            persistEvidence: persistEvidence,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        // The shared writer persists bulk evidence, folds accepted testimony and
        // records completion. Runtime ingestion does not replace schema objects.
        ISubstrateWriter writer = accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory,
            new NpgsqlIngestObservability(ds, persistEvidence,
                loggerFactory.CreateLogger<NpgsqlIngestObservability>()));

        string destination = dec is RepoDecomposer { VerifiedRepository: not null }
            ? "configured PostgreSQL (verified Git corpus)" : LaplaceInstall.RedactConnectionString(ConnString);
        Console.WriteLine($"ingest {dec.GetType().Name} source={dec.SourceName} via IngestRunner → {destination} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)"));
        var sw = Stopwatch.StartNew();
        var options = BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli,
            skipSourceCompletion,
            sizingProfile: dec.SizingProfile);
        // A language scope admits every tag of the languages it names, by ISO 639 testimony.
        options = options with
        {
            DecomposerOptions = options.DecomposerOptions with
            {
                Languages = await LanguageTagExpansion.ExpandAsync(
                    options.DecomposerOptions.Languages, ds).ConfigureAwait(false),
            },
        };
        var result = await runner.RunAsync(dec, options, CancellationToken.None);
        sw.Stop();

        Console.WriteLine(
            $"done: {result.UnitsApplied:N0} intents applied, "
            + $"{result.EntitiesInserted:N0} novel entities, "
            + $"{result.PhysicalitiesInserted:N0} physicalities, "
            + $"{result.TotalRoundTrips:N0} round-trips, "
            + $"{sw.Elapsed.TotalSeconds:F1}s");
        if (result.Failures.Count > 0)
        {
            Console.Error.WriteLine($"failures: {result.Failures.Count}");
            return 1;
        }

        await RegisterDynamicCanonicalsAsync(ds, dec);
        Console.WriteLine($"consensus: {((IConsensusFoldMetrics)writer).CellsFolded:N0} cells materialized during ingest "
                        + $"from {((IConsensusFoldMetrics)writer).ObservationsAccumulated:N0} observations "
                        + "(queued folds drained before success)");

        // ANALYZE and validation counts are not part of the fold; they run only when the
        // ingest added rows.
        long novelRows = result.EntitiesInserted + result.PhysicalitiesInserted
            + result.AttestationsInserted;
        // The statistics are not optional: the site's counts are pg_class.reltuples and
        // pg_stats, so an ANALYZE that skipped a table (the role lacks MAINTAIN on it or on a
        // partition) leaves a seeded database reading as empty. That fails the run, naming the
        // tables (Laplace#1879); the validation counts after it stay advisory.
        AnalyzeIncompleteException? analyzeFailure = null;
        if (novelRows > 0)
        {
            try { await PrintIngestValidationAsync(ds, dec, exactSourceValidation: false); }
            catch (AnalyzeIncompleteException ex) { analyzeFailure = ex; }
            catch (Exception ex)
            { Console.Error.WriteLine($"warn: ingest validation failed (ingest itself is complete): {ex.Message}"); }
        }
        if (dec is RepoDecomposer { VerifiedRepository: not null } repository)
            await WriteVerifiedGitCorpusReceiptAsync(ds, repository, result,
                accumulator.ObservationsAccumulated, accumulator.CellsFolded,
                cli?.GitCorpusReceipt ?? throw new InvalidDataException("Missing corpus receipt path."),
                corpusRuntime ?? throw new InvalidDataException("Missing loaded runtime identity."));
        if (analyzeFailure is not null)
        {
            Console.Error.WriteLine($"error: post-ingest statistics not refreshed (the rows are committed): {analyzeFailure.Message}");
            return 1;
        }
        return 0;
    }

    public static async Task<int> StatsAsync(string? sourceKey = null)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        if (await PrintNativeStatsAsync(ds, sourceKey) is { } nativeResult)
            return nativeResult;
        IDecomposer? decomposer = null;
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            try { decomposer = CliRuntime.Decomposers.Resolve(sourceKey); }
            catch (ArgumentException ex) { return Fail(ex.Message); }
        }
        await PrintIngestValidationAsync(ds, decomposer, exactSourceValidation: true);
        return 0;
    }

    // Close a cut-off journal row ('running' with no live process) through the
    // installed op. The op refuses non-running rows; it does not check liveness, so
    // the caller must know no other process still owns the run.
    public static async Task<int> CloseRunAsync(string runId, string status)
    {
        if (!Guid.TryParse(runId, out var run))
        { Console.Error.WriteLine($"close-run: '{runId}' is not a run_id (uuid)"); return 2; }
        if (status is not ("cancelled" or "failed"))
        { Console.Error.WriteLine($"close-run: status must be cancelled|failed, got '{status}'"); return 2; }
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        try
        {
            await NpgsqlIngestOps.CloseIngestRunAsync(conn, run, status);
        }
        catch (PostgresException ex)
        {
            Console.Error.WriteLine($"close-run: {ex.MessageText}");
            return 1;
        }
        Console.WriteLine($"closed run {run} -> {status}");
        return 0;
    }

    // Checks through the installed op that a source's relation-law bootstrap rows are
    // present. The law relation is supplied by the caller, not embedded here.
    // Exit 0 present, 1 absent.
    public static async Task<int> SourceBootstrapAsync(string sourceName, string lawRelation)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        await using var conn = await ds.OpenConnectionAsync();
        bool present = await NpgsqlIngestOps.SourceBootstrapPresentAsync(
            conn, sourceName, lawRelation);
        Console.WriteLine($"{sourceName}: bootstrap_present={(present ? "true" : "false")}");
        return present ? 0 : 1;
    }

    public static async Task<int> RebuildPhysIndexesAsync()
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        // Every definition is issued each time (CREATE INDEX IF NOT EXISTS), so a
        // partially-indexed database gets whatever is missing.
        Console.WriteLine("ensuring physicalities indexes (CREATE IF NOT EXISTS) ...");
        var sw = Stopwatch.StartNew();
        await SecondaryIndexPolicy.EnsureIndexesAsync(ds, SchemaPhysIndexDefs, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"physicalities secondary indexes ensured in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // The physicalities secondary index set declared by
    // extension/laplace_substrate/sql/indexes/*.sql.in, which is the authority. This
    // recovery command writes exactly this list to a live database, so it must match the
    // extension. The Hilbert btree serves Hilbert-locality equality joins (e.g.
    // structural.anagrams_of) under a primary key of (id).
    private static readonly string[] SchemaPhysIndexDefs =
    [
        SqlCatalog.Get("maintenance.physicalities_entity_btree").Text,
        SqlCatalog.Get("maintenance.physicalities_type_btree").Text,
        SqlCatalog.Get("maintenance.physicalities_coord_gist").Text,
        SqlCatalog.Get("maintenance.physicalities_direction_gist").Text,
        SqlCatalog.Get("maintenance.physicalities_hilbert_btree").Text,
        SqlCatalog.Get("maintenance.physicalities_observed_brin").Text,
        SqlCatalog.Get("maintenance.physicalities_traj_probe").Text,
        SqlCatalog.Get("maintenance.physicalities_traj_first_id_btree").Text,
        SqlCatalog.Get("maintenance.physicalities_constituents_gin").Text,
    ];

    private static async Task RegisterDynamicCanonicalsAsync(
        NpgsqlDataSource ds, IDecomposer decomposer)
    {
        var names = new HashSet<string>(decomposer.CanonicalNamesForReadback, StringComparer.Ordinal);
        // An explicitly supplied witness id need not use the conventional source
        // namespace. Register only names whose canonical identity is that witness.
        if (SubstrateCanonicalIds.Source(decomposer.SourceName) == decomposer.SourceId)
            names.Add($"substrate/source/{decomposer.SourceName}/v1");
        else if (Hash128.Text(decomposer.SourceName) == decomposer.SourceId)
            names.Add(decomposer.SourceName);
        if (names.Count == 0) return;
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, names);
        Console.WriteLine($"registered {names.Count:N0} canonical names");
    }

    private static async Task PrintIngestValidationAsync(
        NpgsqlDataSource ds,
        IDecomposer? decomposer,
        bool exactSourceValidation)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var phase = Stopwatch.StartNew();

        // After bulk COPY the planner statistics may predate the load. Refresh the columns
        // the read paths use; column-scoped so PostGIS ND-stats on
        // physicalities.coord/trajectory are not recomputed.
        await NpgsqlIngestOps.AnalyzePostIngestValidationAsync(conn);
        long analyzeMs = phase.ElapsedMilliseconds;
        phase.Restart();

        Task<long> EvidenceForSource(string sourceKey) =>
            NpgsqlIngestOps.EvidenceCountForSourceNameAsync(conn, sourceKey);
        Task<long> ContentForSource(string sourceKey) =>
            NpgsqlIngestOps.ContentCountForSourceNameAsync(conn, sourceKey);
        Task<long> RelationEvidence(string relationType, string? sourceKey = null) =>
            NpgsqlIngestOps.EvidenceCountForRelationAsync(conn, relationType, sourceKey);
        Task<long> RelationEvidenceForSourceId(string relationType, Hash128 sourceId) =>
            NpgsqlIngestOps.EvidenceCountForRelationAndSourceIdAsync(
                conn, relationType, sourceId.ToBytes());

        Console.WriteLine("substrate totals (ops.substrate_counts(): pg_class.reltuples ESTIMATE is not count(*); MEASURED values are retained assertion counts):");
        {
            var counts = await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, CancellationToken.None);
            foreach (var row in counts)
                Console.WriteLine($"  {row.Metric,-32}: {row.Value?.ToString("N0") ?? "Not measured",12}");
        }
        long summaryMs = phase.ElapsedMilliseconds;
        Console.WriteLine(
            $"LAPSIGHT_POST_INGEST analyze_ms={analyzeMs} "
            + $"summary_ms={summaryMs} exact_source_validation={exactSourceValidation.ToString().ToLowerInvariant()}");

        // Exact per-source content attribution is an unbounded diagnostic scan, not part of
        // committing an ingest; after an ingest it is deferred to `laplace stats <cli-source>`.
        if (decomposer is not null && !exactSourceValidation)
        {
            Console.WriteLine(
                $"  witness [{decomposer.SourceName}] exact source counts deferred "
                + "(workflow gate; explicit: laplace-mono stats <cli-source>)");
            return;
        }

        if (decomposer is null)
        {
            // All-source view reads evidence per source only (attestations_source_btree),
            // bounded by a 120 s timeout. Per-source content is `stats <source>`.
            Console.WriteLine("  witnesses (evidence per source; content: run `stats <source>`):");
            try
            {
                foreach (var row in await NpgsqlIngestOps.AttestationCountsBySourceAsync(conn, timeoutSeconds: 120))
                {
                    var label = row.Source ?? $"<unrendered source {row.SourceId ?? "(null)"}>";
                    Console.WriteLine($"    {label,-44}: {row.Evidence,12:N0} att");
                }
            }
            catch (Exception ex) when (ex is NpgsqlException { InnerException: TimeoutException } or TimeoutException)
            {
                Console.WriteLine("    (source grouping exceeded 120s — per-source: `stats <source>`, exact via ops.evidence_count)");
            }
            return;
        }

        string srcKey = decomposer.SourceName;
        long att = await EvidenceForSource(srcKey);
        long content = await ContentForSource(srcKey);
        bool layerOk = await NpgsqlIngestOps.LayerCompletedAsync(conn, decomposer.LayerOrder, decomposer.SourceId);
        Console.WriteLine($"  witness [{srcKey}] L{decomposer.LayerOrder}: {att:N0} attestations, {content:N0} content, layer_complete={layerOk}");

        // Source-content receipt: every relation the decomposer declares, counted under the
        // source id stamped on its evidence, so a declared-but-empty relation prints evidence=0.
        foreach (string relation in decomposer.DeclaredRelations.Distinct(StringComparer.Ordinal))
        {
            long evidence = await RelationEvidenceForSourceId(relation, decomposer.SourceId);
            Console.WriteLine(
                $"SEED_CONTENT_RECEIPT source={srcKey} source_id={decomposer.SourceId} "
                + $"relation={relation} evidence={evidence}");
        }

        if (decomposer.LayerOrder == 10)
        {
            // A model's source id is the content hash of its snapshot; its evidence is
            // counted per relation under that id, alongside circuit trajectories.
            byte[] srcId = decomposer.SourceId.ToBytes();
            Task<long> Rel(string rel) =>
                NpgsqlIngestOps.EvidenceCountForRelationAndSourceIdAsync(conn, rel, srcId);
            long merges = await Rel("MERGES_WITH");
            long occ = await Rel("APPEARS_IN");
            long structure = await Rel("CONTAINS") + await Rel("PRECEDES");
            long evidence = await Rel("SIMILAR_TO") + await Rel("ATTENDS")
                            + await Rel("OV_RELATES") + await Rel("COMPLETES_TO");
            long circuits = await NpgsqlIngestOps.ModelCircuitTrajectoryCountAsync(conn);
            Console.WriteLine(
                $"  check model deposition: circuit_evidence={evidence:N0} merges={merges:N0} "
                + $"appears_in={occ:N0} structure={structure:N0} circuit_trajectories={circuits:N0} "
                + "(source = content hash, trust=AIModelProbe)");
            return;
        }

        switch (srcKey)
        {
            case "Unicode":
                {
                    var probe = await NpgsqlIngestOps.UnicodeCapitalAContentProbeAsync(conn);
                    if (probe.Count > 0)
                    {
                        var r = probe[0];
                        Console.WriteLine("  check U+0041 'A':");
                        Console.WriteLine($"    render  : {r.Render}  tier={r.Tier}");
                        Console.WriteLine($"    coord   : ({r.X:F6}, {r.Y:F6}, {r.Z:F6}, {r.M:F6})");
                    }
                    else Console.WriteLine("  FAIL: no Unicode CONTENT for U+0041");
                    long uniProv = await EvidenceForSource("Unicode");
                    Console.WriteLine($"    provenance: {uniProv:N0} Unicode attestations");
                    break;
                }
            case "ISO639Decomposer":
                {
                    // Every ISO 639 code is one relation; the scheme is the claim's qualifier.
                    long langs = await RelationEvidence(ISOSource.CodeRelation, srcKey);
                    Console.WriteLine($"  check languages: {langs:N0} ISO code attestations");
                    break;
                }
            case "WordNetDecomposer":
                Console.WriteLine($"  check wordnet: IS_A={await RelationEvidence("IS_A", srcKey):N0} "
                                + $"HAS_SENSE={await RelationEvidence("HAS_SENSE", srcKey):N0} "
                                + $"HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0}");
                break;
            case "VerbNetDecomposer":
                Console.WriteLine($"  check verbnet: HAS_VERB_FRAME={await RelationEvidence("HAS_VERB_FRAME", srcKey):N0} "
                                + $"HAS_THEMATIC_ROLE={await RelationEvidence("HAS_THEMATIC_ROLE", srcKey):N0}");
                break;
            case "PropBank":
                Console.WriteLine($"  check propbank: HAS_SEMANTIC_ROLE={await RelationEvidence("HAS_SEMANTIC_ROLE", srcKey):N0} "
                                + $"HAS_SENSE={await RelationEvidence("HAS_SENSE", srcKey):N0}");
                break;
            case "Atomic2020Decomposer":
                Console.WriteLine($"  check atomic: CAUSES={await RelationEvidence("CAUSES", srcKey):N0} "
                                + $"X_WANT={await RelationEvidence("X_WANT", srcKey):N0}");
                break;
            case "ConceptNetDecomposer":
                Console.WriteLine($"  check conceptnet: RelatedTo={await RelationEvidence("RELATED_TO", srcKey):N0} "
                                + $"IsA={await RelationEvidence("IS_A", srcKey):N0}");
                break;
            case "TatoebaDecomposer":
                Console.WriteLine($"  check tatoeba: IS_TRANSLATION_OF={await RelationEvidence("IS_TRANSLATION_OF", srcKey):N0} "
                                + $"HAS_LANGUAGE={await RelationEvidence("HAS_LANGUAGE", srcKey):N0}");
                break;
            case "WiktionaryDecomposer":
                Console.WriteLine($"  check wiktionary: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0} "
                                + $"HAS_EXAMPLE={await RelationEvidence("HAS_EXAMPLE", srcKey):N0}");
                break;
            case "OMWDecomposer":
                Console.WriteLine($"  check omw: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0}");
                break;
            case "CILIDecomposer":
                Console.WriteLine($"  check cili: HAS_DEFINITION={await RelationEvidence("HAS_DEFINITION", srcKey):N0} "
                                + $"{CILISource.Relations[2]}={await RelationEvidence(CILISource.Relations[2], srcKey):N0} "
                                + $"IS_TYPED_AS={await RelationEvidence("IS_TYPED_AS", srcKey):N0}");
                break;
            case "FrameNet":
                Console.WriteLine($"  check framenet: HAS_FRAME_ELEMENT={await RelationEvidence("HAS_FRAME_ELEMENT", srcKey):N0}");
                break;
            case "SemLinkDecomposer":
                Console.WriteLine($"  check semlink: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "MapNetDecomposer":
                Console.WriteLine($"  check mapnet: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "WordFrameNetDecomposer":
                Console.WriteLine($"  check wordframenet: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
                break;
            case "OpenSubtitlesDecomposer":
                Console.WriteLine($"  check opensubtitles: IS_TRANSLATION_OF={await RelationEvidence("IS_TRANSLATION_OF", srcKey):N0}");
                break;
            default:
                Console.WriteLine($"  check: {att:N0} attestations from this witness");
                break;
        }
    }

}
