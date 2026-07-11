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
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.PropBank;
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

internal static class IngestCommands
{
    internal sealed record IngestCliArgs(
        string Source,
        string Path,
        LanguageFilter? LangOverride,
        bool? EmitCrossLanguageLinks,
        bool SkipEvidence,
        bool RegisterOnly,
        bool Force = false,
        bool NoAnalyze = false,
        bool Recursive = false);


    private static IngestCliArgs ParseIngestCliArgs(string[] args)
    {
        var rest = new List<string>(args);
        LanguageFilter? langs = null;
        bool? emitCross = null;
        bool skipEvidence = false;
        bool registerOnly = false;
        bool force = false;
        bool noAnalyze = false;
        bool recursive = false;
        for (int i = 0; i < rest.Count;)
        {
            if (rest[i] == "--langs" && i + 1 < rest.Count)
            {
                langs = LanguageFilter.FromSpec(rest[i + 1]);
                rest.RemoveAt(i + 1);
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--emit-cross-lang")
            {
                emitCross = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--no-evidence")
            {
                skipEvidence = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--register-only")
            {
                registerOnly = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--force")
            {
                force = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--no-analyze")
            {
                noAnalyze = true;
                rest.RemoveAt(i);
            }
            else if (rest[i] == "--recursive")
            {
                recursive = true;
                rest.RemoveAt(i);
            }
            else i++;
        }
        return new(
            rest.Count > 0 ? rest[0] : "",
            rest.Count > 1 ? rest[1] : "",
            langs,
            emitCross,
            skipEvidence,
            registerOnly,
            force,
            noAnalyze,
            recursive);
    }

    private static bool ResolvePersistEvidence(IngestCliArgs? cli)
    {
        if (cli?.SkipEvidence == true) return false;
        return ConsensusAccumulatingWriter.ResolvePersistEvidence();
    }

    public static async Task<int> IngestAsync(string[] args)
    {
        var cli = ParseIngestCliArgs(args);
        if (string.IsNullOrEmpty(cli.Source))
            return Fail("usage: laplace ingest <source> [path] [--langs en,...] [--emit-cross-lang] [--no-evidence]\n"
                        + "  sources: unicode | iso639 | wordnet | omw | ud | tatoeba | atomic2020 | conceptnet | wiktionary | framenet | opensubtitles | verbnet | propbank | semlink | mapnet | wordframenet | code | repo | tabular | tiny-codes | stack | safetensors | image | audio | document\n"
                        + "  --langs: language scope for this run\n"
                        + "  --no-evidence: fold consensus only; skip laplace.attestations");




        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        string sourceKey = cli.Source.ToLowerInvariant();

        if (IngestDispatchTable.TryDispatch(sourceKey, cli, out var task))
            return await task;

        return Fail($"unknown ingest source '{cli.Source}' (supported: {string.Join(", ", IngestDispatchTable.RegisteredKeys.OrderBy(k => k))})");
    }

    internal static async Task<int> OmwProbeAsync(IngestCliArgs cli)
    {
        string wns = IngestDataPaths.Resolve("omw", cli.Path);
        if (!Directory.Exists(wns))
            return Fail($"OMW path not found: {wns}");

        long start = 0;
        long max = 0;
        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        Console.Error.WriteLine($"omw-probe: scanning {wns} start_row={start} max_rows={(max > 0 ? max.ToString() : "all")}");
        var fail = await OmwComposeProbe.ScanFirstFailureAsync(wns, cli.LangOverride, start, max);
        if (fail is null)
        {
            Console.Error.WriteLine("omw-probe: all rows passed probe+materialize_phys");
            return 0;
        }

        Console.Error.WriteLine(
            $"omw-probe: FAIL row={fail.RowIndex} file={fail.FilePath}\n"
            + $"  error={fail.Error}\n"
            + $"  bytes={fail.LineBytes} preview={fail.LinePreview}");
        return 1;
    }

    internal static async Task<int> IngestSafetensorSnapshotAsync(string modelDir, IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(modelDir))
            return Fail("usage: laplace ingest safetensors <snapshot-dir>\n"
                        + "  HF snapshot: config.json + tokenizer.json + *.safetensors\n"
                        + "  (safetensors are not self-contained like GGUF — the directory is the witness unit)");

        var snapshotCheck = SafetensorSnapshotWitness.Validate(modelDir);
        if (!snapshotCheck.Ok)
            return Fail($"invalid safetensor snapshot: {snapshotCheck.Error}\n"
                        + $"path: {modelDir}");

        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();







        var dsb = new NpgsqlDataSourceBuilder(ConnString);
        dsb.ConnectionStringBuilder.CommandTimeout = 0;
        await using var ds = dsb.Build();

        var dec = new ModelDecomposer(modelDir, persistEvidence: ResolvePersistEvidence(cli));




        if (cli.RegisterOnly)
        {
            await RegisterDynamicCanonicalsAsync(ds, dec);
            return 0;
        }

        var (modelSource, modelName) = ModelDecomposer.SourceForModel(modelDir);
        // Analyzer modes (LAPLACE_MODEL_PLANES != "structure") are calculated
        // re-passes over an already-recorded model; the recorder's re-deposition
        // guard must not block them.
        if (Laplace.Decomposers.Model.ModelTokenEdgeETL.ResolvePlanesMode() == "structure")
        await using (var chkConn = await ds.OpenConnectionAsync())
        {
            await using var chkCmd = chkConn.CreateCommand();
            chkCmd.CommandText =
                "SELECT laplace.evidence_count(p_type => $2, p_source => $1) > 0";
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = modelSource.ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            chkCmd.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = Laplace.Ingestion.LayerCompletion.RelationTypeId(dec.LayerOrder).ToBytes(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
            bool alreadyIngested = (bool)(await chkCmd.ExecuteScalarAsync() ?? false);
            if (alreadyIngested)
            {
                Console.WriteLine($"Safetensor snapshot already deposited — source {modelName}: {modelSource}");
                Console.WriteLine($"(re-deposition refused to prevent consensus contamination; "
                                  + $"reset with db-fresh to test from scratch)");
                return 0;
            }
        }

        var loggerFactory = ConsoleLoggerProvider.Factory();
        var inner = new NpgsqlSubstrateWriter(ds,
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());





        bool persistEvidenceResolved = ResolvePersistEvidence(cli);
        var accumulator = new ConsensusAccumulatingWriter(inner, ds,
            freshSource: false,
            persistEvidence: persistEvidenceResolved,
            stageAsWalks: !persistEvidenceResolved,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        ISubstrateWriter writer = accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);
        Console.WriteLine("mode: safetensor snapshot apply (anti-join merge; consensus accumulates at ingest)");

        Console.WriteLine($"deposit safetensor snapshot {modelDir} via IngestRunner → {ConnString} ...");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await runner.RunAsync(
                dec,
                BuildIngestOptions(sw, dec.SourceName, skipLayerCheck: true, ecosystemPath: null, cli,
                    // Analyzer modes are calculated re-passes over an already-recorded
                    // model (doc 08's chess-analyze pattern): the recorder's
                    // completion marker must neither block them nor be re-written.
                    skipSourceCompletion:
                        Laplace.Decomposers.Model.ModelTokenEdgeETL.ResolvePlanesMode() != "structure")
                with
                {
                    DecomposerOptions =                     DecomposerOptions.ForWitness(
                    dec.SourceName,
                    IngestSizing.Resolve(
                        IngestTopology.Current.PerformanceCoreCount,
                        IngestTopology.Current.FileWorkers,
                        IngestTopology.Current.ApplyPartitions).RecordBatchSize,
                    cli.LangOverride,
                    cli.EmitCrossLanguageLinks)
                },
                CancellationToken.None);
            sw.Stop();

            Console.WriteLine(
                $"done: {result.UnitsApplied:N0} intents applied, "
                + $"{result.EntitiesInserted:N0} novel entities, "
                + $"{result.AttestationsInserted:N0} attestations, "
                + $"{result.TotalRoundTrips:N0} round-trips, "
                + $"{sw.Elapsed.TotalSeconds:F1}s");
            if (result.Failures.Count > 0)
            {
                Console.Error.WriteLine($"failures: {result.Failures.Count}");
                foreach (var f in result.Failures.Take(5))
                    Console.Error.WriteLine($"  {f}");
                return 1;
            }





            await RegisterDynamicCanonicalsAsync(ds, dec);

            Console.WriteLine(
                $"consensus: folding {accumulator.ObservationsAccumulated:N0} matches "
                + $"across {accumulator.FoldWorkers} partition(s) ...");
            var matSw = Stopwatch.StartNew();
            var materialized = await accumulator.MaterializeConsensusAsync();
            matSw.Stop();
            Console.WriteLine(
                $"consensus: {materialized:N0} relations materialized from "
                + $"{accumulator.ObservationsAccumulated:N0} matches in {matSw.Elapsed.TotalSeconds:F1}s "
                + $"(accumulated at ingest; evidence = provenance-only)");
        }
        finally
        {
            sw.Stop();
        }
        try { await PrintIngestValidationAsync(ds, dec); }
        catch (Exception ex)
        { Console.Error.WriteLine($"warn: safetensor deposition validation failed: {ex.Message}"); }
        return 0;
    }

    internal static async Task<int> IngestDocumentAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace ingest document <file-or-directory>\n"
                        + "  Deposits whole documents (entities + physicalities + PRECEDES bigrams).\n"
                        + "  Bit-perfect proof: laplace db-roundtrip <file>  (reconstruct + compare).");
        if (!File.Exists(cli.Path) && !Directory.Exists(cli.Path))
            return Fail($"ingest document: path not found: {cli.Path}");

        return await IngestViaRunnerAsync(
            new DocumentDecomposer(),
            Path.GetFullPath(cli.Path),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: true);
    }

    internal static async Task<int> IngestRecipeAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace ingest recipe <recipe.json>\n"
                        + "  Deposits a Mold-A-Model recipe (the simulated UI POST) as a content-addressed\n"
                        + "  Model_Recipe entity, fetchable by export via laplace.model_recipes() / --recipe-from.");
        if (!File.Exists(cli.Path))
            return Fail($"ingest recipe: file not found: {cli.Path}");
        return await IngestViaRunnerAsync(
            new RecipeDecomposer(Path.GetFullPath(cli.Path)),
            Path.GetFullPath(cli.Path),
            skipLayerCheck: true,
            cli,
            skipSourceCompletion: true);
    }

    internal static async Task<int> IngestUnicodeViaRunnerAsync(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new UnicodeDecomposer(), IngestDataPaths.Resolve("unicode", cli.Path), skipLayerCheck: true, cli);

    internal static async Task<int> IngestISO639Async(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new ISODecomposer(), IngestDataPaths.Resolve("iso639", cli.Path), skipLayerCheck: false, cli);

    private static string ResolveIngestPath(string? cliPath, string defaultPath)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(cliPath) ? defaultPath : cliPath);

    private static string? ResolveRequiredIngestPath(string? cliPath)
        => string.IsNullOrWhiteSpace(cliPath) ? null : Path.GetFullPath(cliPath);

    internal static async Task<int> IngestCodeAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest code <file-or-directory>");
        return await IngestViaRunnerAsync(new CodeDecomposer(), path, skipLayerCheck: true, cli);
    }

    internal static async Task<int> IngestRepoAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest repo <repository-root>");
        return await IngestViaRunnerAsync(new RepoDecomposer(), path, skipLayerCheck: true, cli);
    }

    internal static async Task<int> IngestTabularAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest tabular <file-or-directory>");
        return await IngestViaRunnerAsync(new TabularDecomposer(), path, skipLayerCheck: true, cli);
    }

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath,
        IngestCliArgs? cli = null, bool skipSourceCompletion = false,
        IngestSourceProfile? sizingProfile = null)
    {
        IngestTopology.EnsureReady();
        long lastMs = -10_000;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastMs < 2000) return;
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
        int workers = IngestTopology.Current.CommitWorkers;
        // LAPLACE_INGEST_MAX_UNITS caps INPUT VOLUME (smoke/bench scoping — an
        // operator decision, like --langs). It is not a machine-sizing knob:
        // batch/commit sizing stays owned by IngestSizing/MemoryTopology and
        // deliberately has no env override.
        long maxUnits =
            long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_INGEST_MAX_UNITS"),
                out var mu) && mu > 0 ? mu : 0;
        var profile = sizingProfile ?? IngestSourceProfile.Default;
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
            SkipSourceCompletion = skipSourceCompletion,
            EcosystemPath = ecosystemPath,
            BatchSize = batch,
            DecomposerOptions = decoOpts,
            CommitRows = commitRows,
            ParallelWorkers = workers,
            Progress = progress,
            RetryPolicy = workers > 1
                                            ? TransientErrorRetryPolicy.ConcurrencyRetry
                                            : TransientErrorRetryPolicy.NoRetry,
            AbortOnTransientExhaustion = true,
        };
    }

    internal static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        CodepointPerfcache.Load(ResolveBlob());
        HighwayPerfcache.LoadDefault();

        LanguageReference.EnsureLoaded();
        var topo = IngestTopology.EnsureReady();

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var loggerFactory = ConsoleLoggerProvider.Factory();
        bool force = cli?.Force ?? false;
        var innerWriter = new NpgsqlSubstrateWriter(ds,
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>());
        bool persistEvidence = ResolvePersistEvidence(cli);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            freshSource: false,
            persistEvidence: persistEvidence,
            stageAsWalks: !persistEvidence,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var writer = (ISubstrateWriter)accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)"));
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli,
                skipSourceCompletion || (cli?.Force ?? false),
                sizingProfile: dec.SizingProfile),
            CancellationToken.None);
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
        Console.WriteLine(
            $"consensus: folding {accumulator.ObservationsAccumulated:N0} matches "
            + $"across {accumulator.FoldWorkers} partition(s) ...");
        var materialized = await accumulator.MaterializeConsensusAsync();
        Console.WriteLine($"consensus: {materialized:N0} relations materialized "
                        + $"(accumulated at ingest; evidence = provenance-only)");

        try { await PrintIngestValidationAsync(ds, dec); }
        catch (Exception ex)
        { Console.Error.WriteLine($"warn: ingest validation failed (ingest itself is complete): {ex.Message}"); }
        return 0;
    }

    public static async Task<int> StatsAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await PrintIngestValidationAsync(ds, decomposer: null);
        return 0;
    }

    // Restore secondary indexes a killed or crashed bulk ingest left dropped (journaled in
    // laplace.index_cycle_journal by NpgsqlIndexCycle). The pipeline recovers automatically at
    // the START of the next bulk run — this is the ops entry for the window in between, when
    // every non-pkey query on attestations/consensus/entities is a full scan. Refreshes
    // planner statistics afterwards: freshly built indexes without stats still plan badly.
    public static async Task<int> RecoverCycledIndexesAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();

        async Task<long> JournalCountAsync()
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM laplace.index_cycle_journal";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        long pending = await JournalCountAsync();
        if (pending == 0)
        {
            Console.WriteLine("index-cycle journal empty — nothing to recover");
            return 0;
        }

        Console.WriteLine($"recovering {pending} journaled secondary index(es) — serial builds ...");
        var log = ConsoleLoggerProvider.Factory().CreateLogger("index-cycle");
        var sw = Stopwatch.StartNew();
        await NpgsqlIndexCycle.RecoverAsync(ds, log, CancellationToken.None);

        Console.WriteLine("refreshing planner statistics ...");
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var stats = conn.CreateCommand())
        {
            stats.CommandTimeout = 0;
            stats.CommandText =
                "ANALYZE laplace.attestations; ANALYZE laplace.consensus; ANALYZE laplace.entities";
            await stats.ExecuteNonQueryAsync();
        }
        sw.Stop();

        long remaining = await JournalCountAsync();
        Console.WriteLine(
            $"recovered {pending - remaining}/{pending} index(es) in {sw.Elapsed.TotalSeconds:F0}s"
            + (remaining > 0 ? $" — {remaining} still journaled (rerun)" : ""));
        return remaining == 0 ? 0 : 1;
    }

    public static async Task<int> RebuildPhysIndexesAsync()
    {
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var indexPolicy = new SecondaryIndexPolicy(ds);
        if (await indexPolicy.SecondaryIndexesPresentAsync("physicalities", CancellationToken.None))
        {
            Console.WriteLine("physicalities secondary indexes already present");
            return 0;
        }
        Console.WriteLine("creating missing physicalities indexes (CREATE IF NOT EXISTS) ...");
        var sw = Stopwatch.StartNew();
        await SecondaryIndexPolicy.EnsureIndexesAsync(ds, SchemaPhysIndexDefs, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"physicalities secondary indexes ensured in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static readonly string[] SchemaPhysIndexDefs =
    [
        "CREATE INDEX IF NOT EXISTS physicalities_entity_btree ON laplace.physicalities USING btree (entity_id)",
        "CREATE INDEX IF NOT EXISTS physicalities_type_btree ON laplace.physicalities USING btree (type)",
        "CREATE INDEX IF NOT EXISTS physicalities_coord_gist ON laplace.physicalities USING gist (coord gist_geometry_ops_nd)",
        "CREATE INDEX IF NOT EXISTS physicalities_hilbert_btree ON laplace.physicalities USING btree (hilbert_index)",
        "CREATE INDEX IF NOT EXISTS physicalities_radius_btree ON laplace.physicalities USING btree (radius_origin)",
        "CREATE INDEX IF NOT EXISTS physicalities_residual_btree ON laplace.physicalities USING btree (alignment_residual) WHERE alignment_residual IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS physicalities_observed_brin ON laplace.physicalities USING brin (observed_at)",
        "CREATE INDEX IF NOT EXISTS physicalities_traj_probe ON laplace.physicalities USING btree (observed_at) WHERE type = 1 AND trajectory IS NOT NULL",
        "CREATE INDEX IF NOT EXISTS physicalities_constituents_gin ON laplace.physicalities USING gin (public.laplace_trajectory_constituent_ids(trajectory)) WHERE type = 1 AND trajectory IS NOT NULL",
    ];

    private static async Task RegisterDynamicCanonicalsAsync(
        NpgsqlDataSource ds, IDecomposer decomposer)
    {
        var names = new HashSet<string>(decomposer.CanonicalNamesForReadback, StringComparer.Ordinal);
        names.Add($"substrate/source/{decomposer.SourceName}/v1");
        if (names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new global::Npgsql.NpgsqlParameter
        {
            ParameterName = "names",
            Value = names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"registered {names.Count:N0} canonical names");
    }

    private static async Task PrintIngestValidationAsync(NpgsqlDataSource ds, IDecomposer? decomposer)
    {
        await using var conn = await ds.OpenConnectionAsync();

        // Immediately after a bulk COPY ingest the just-loaded tables can still carry
        // pre-load planner statistics (autoanalyze has not necessarily caught up). With a
        // stale reltuples≈0 the planner picks a nested loop for content_count/evidence_count
        // and — because these validation commands run with CommandTimeout=0 — the query
        // hangs indefinitely instead of finishing in ~1s. Refresh the stats the validations
        // depend on before running them. Column-scoped so we skip the minutes-long PostGIS
        // ND-stats on physicalities.coord/trajectory (never touched by these counts); a
        // column-list ANALYZE still refreshes pg_class.reltuples, which is the estimate that
        // matters here.
        await using (var an = conn.CreateCommand())
        {
            an.CommandTimeout = 0;
            an.CommandText =
                "ANALYZE laplace.attestations (subject_id, source_id, type_id, object_id); "
                + "ANALYZE laplace.physicalities (entity_id, type); "
                + "ANALYZE laplace.entities (id, tier, type_id); "
                // consensus is freshly folded here; senses()/define()/edge reads plan against
                // it, and without stats the planner picks a nested loop that never returns under
                // CommandTimeout=0 (the WordNet 'dog' confirmation query hung ~17 min on this).
                + "ANALYZE laplace.consensus (subject_id, type_id, object_id, rating, rd);";
            await an.ExecuteNonQueryAsync();
        }


        NpgsqlCommand Cmd()
        {
            var c = conn.CreateCommand();
            c.CommandTimeout = 0;
            return c;
        }

        async Task<long> EvidenceForSource(string sourceKey)
        {
            await using var c = Cmd();
            c.CommandText = "SELECT laplace.evidence_count(p_source => laplace.source_id($1))";
            c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<long> ContentForSource(string sourceKey)
        {
            await using var c = Cmd();
            c.CommandText = "SELECT laplace.content_count(p_source => laplace.source_id($1))";
            c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<long> RelationEvidence(string relationType, string? sourceKey = null)
        {
            await using var c = Cmd();
            c.CommandText = sourceKey is null
                ? "SELECT laplace.evidence_count(p_type => laplace.relation_type_id($1))"
                : "SELECT laplace.evidence_count(p_type => laplace.relation_type_id($1), p_source => laplace.source_id($2))";
            c.Parameters.AddWithValue(relationType);
            if (sourceKey is not null) c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<bool> LayerMarkedComplete(int layer, string sourceKey)
        {
            await using var c = Cmd();
            c.CommandText =
                "SELECT laplace.evidence_count("
                + "p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/' || $1 || '/v1'), "
                + "p_source => laplace.source_id($2)) > 0";
            c.Parameters.AddWithValue(layer);
            c.Parameters.AddWithValue(sourceKey);
            return (bool)(await c.ExecuteScalarAsync() ?? false);
        }

        Console.WriteLine("substrate counts (pg_class.reltuples ESTIMATE — not count(*); run ANALYZE or evidence_count() for exact):");
        {
            await using var counts = Cmd();
            counts.CommandText = "SELECT metric, value FROM laplace.substrate_counts()";
            await using var rdr = await counts.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                Console.WriteLine($"  {rdr.GetString(0),-32}: {rdr.GetInt64(1),12:N0}");
        }

        if (decomposer is null)
        {
            // laplace.source_counts() joins a count(DISTINCT physicalities) per source —
            // unbounded at 135M attestations; `stats` hung for minutes (Issue 52). The
            // evidence half alone walks attestations_source_btree in ~30s live. Content
            // per source stays exact via `stats <source>` (content_count is per-source
            // bounded).
            Console.WriteLine("  witnesses (evidence per source; content: run `stats <source>`):");
            try
            {
                await using var src = Cmd();
                src.CommandText = """
                    SELECT laplace.render(a.source_id) AS source, count(*) AS evidence
                    FROM laplace.attestations a
                    GROUP BY a.source_id
                    ORDER BY evidence DESC
                    """;
                src.CommandTimeout = 120;
                await using var rdr = await src.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    Console.WriteLine($"    {rdr.GetString(0),-44}: {rdr.GetInt64(1),12:N0} att");
            }
            catch (Exception ex) when (ex is NpgsqlException { InnerException: TimeoutException } or TimeoutException)
            {
                Console.WriteLine("    (source grouping exceeded 120s — per-source: `stats <source>`, exact via laplace.evidence_count)");
            }
            return;
        }

        string srcKey = decomposer.SourceName;
        long att = await EvidenceForSource(srcKey);
        long content = await ContentForSource(srcKey);
        bool layerOk = await LayerMarkedComplete(decomposer.LayerOrder, srcKey);
        Console.WriteLine($"  witness [{srcKey}] L{decomposer.LayerOrder}: {att:N0} attestations, {content:N0} content, layer_complete={layerOk}");

        if (decomposer.LayerOrder == 10)
        {
            // Lane-true model validation (Issue 53b): a model's source id is a CONTENT
            // hash (never source_id(name)), and the recorder/analyzer emit
            // TOKEN_MAPS_TO/MERGES_WITH/APPEARS_IN + per-circuit Projection
            // trajectories — not the retired tensor-role relations.
            byte[] srcId = decomposer.SourceId.ToBytes();
            async Task<long> Rel(string rel)
            {
                await using var c = Cmd();
                c.CommandText = "SELECT laplace.evidence_count("
                    + "p_type => laplace.relation_type_id($1), p_source => $2)";
                c.Parameters.AddWithValue(rel);
                c.Parameters.Add(new global::Npgsql.NpgsqlParameter { Value = srcId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
                return (long)(await c.ExecuteScalarAsync() ?? 0L);
            }
            long maps = await Rel("TOKEN_MAPS_TO");
            long merges = await Rel("MERGES_WITH");
            long occ = await Rel("APPEARS_IN");
            long structure = await Rel("CONTAINS") + await Rel("PRECEDES");
            long circuits;
            await using (var c = Cmd())
            {
                c.CommandText = "SELECT count(*) FROM laplace.physicalities p "
                    + "JOIN laplace.entities e ON e.id = p.entity_id "
                    + "WHERE p.type = 3 AND e.type_id = laplace.canonical_id('Model_Circuit')";
                circuits = (long)(await c.ExecuteScalarAsync() ?? 0L);
            }
            Console.WriteLine(
                $"  check model deposition: maps_to={maps:N0} merges={merges:N0} "
                + $"appears_in={occ:N0} structure={structure:N0} circuit_trajectories={circuits:N0} "
                + "(source = content hash, trust=AIModelProbe)");
            return;
        }

        switch (srcKey)
        {
            case "UnicodeDecomposer":
                {



                    await using var cmd = Cmd();
                    cmd.CommandText = @"SELECT laplace.render(laplace.canonical_id('A')), f.tier,
                                           p.x, p.y, p.z, p.m, encode(p.hilbert_index, 'hex')
                                    FROM laplace.entity_facets(laplace.canonical_id('A')) f
                                    CROSS JOIN laplace.entity_physicalities(laplace.canonical_id('A')) p
                                    WHERE p.type = 1";
                    await using var rdr = await cmd.ExecuteReaderAsync();
                    if (await rdr.ReadAsync())
                    {
                        Console.WriteLine("  check U+0041 'A':");
                        Console.WriteLine($"    render  : {rdr.GetString(0)}  tier={rdr.GetInt16(1)}");
                        Console.WriteLine($"    coord   : ({rdr.GetDouble(2):F6}, {rdr.GetDouble(3):F6}, {rdr.GetDouble(4):F6}, {rdr.GetDouble(5):F6})");
                    }
                    else Console.WriteLine("  FAIL: no Unicode CONTENT for U+0041");
                    await rdr.CloseAsync();
                    long uniProv = await EvidenceForSource("UnicodeDecomposer");
                    Console.WriteLine($"    provenance: {uniProv:N0} UnicodeDecomposer attestations");
                    break;
                }
            case "ISO639Decomposer":
                {
                    long langs = await RelationEvidence("HAS_ISO639_3_CODE", srcKey)
                               + await RelationEvidence("HAS_ISO639_1_CODE", srcKey)
                               + await RelationEvidence("HAS_ISO639_2_CODE", srcKey);
                    Console.WriteLine($"  check languages: {langs:N0} ISO code attestations");
                    break;
                }
            case "WordNetDecomposer":
                {
                    await using var cmd = Cmd();
                    cmd.CommandText = @"
                    SELECT laplace.word_id('dog') IS NOT NULL AS dog_ok,
                           (SELECT count(*) FROM laplace.senses(laplace.word_id('dog'))) AS sense_n,
                           (SELECT definition FROM laplace.define(laplace.word_id('dog'), 1) LIMIT 1) AS gloss,
                           laplace.evidence_count(p_type => laplace.relation_type_id('IS_A'),
                                                  p_source => laplace.source_id('WordNetDecomposer')) AS is_a_n,
                           laplace.evidence_count(p_type => laplace.relation_type_id('HAS_SENSE'),
                                                  p_source => laplace.source_id('WordNetDecomposer')) AS has_sense_n";
                    await using var rdr = await cmd.ExecuteReaderAsync();
                    if (await rdr.ReadAsync())
                    {
                        bool dogOk = rdr.GetBoolean(0);
                        long senses = rdr.GetInt64(1);
                        string? gloss = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                        long isA = rdr.GetInt64(3);
                        long hasSense = rdr.GetInt64(4);
                        Console.WriteLine($"  check wordnet/dog: id_ok={dogOk} senses={senses:N0} IS_A={isA:N0} HAS_SENSE={hasSense:N0}");
                        if (gloss is not null) Console.WriteLine($"    define  : {gloss}");
                        if (!dogOk || senses == 0) Console.WriteLine("  FAIL: wordnet lexicon not queryable");
                    }
                    break;
                }
            case "VerbNetDecomposer":
                Console.WriteLine($"  check verbnet: HAS_VERB_FRAME={await RelationEvidence("HAS_VERB_FRAME", srcKey):N0} "
                                + $"HAS_THEMATIC_ROLE={await RelationEvidence("HAS_THEMATIC_ROLE", srcKey):N0}");
                break;
            case "PropBankDecomposer":
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
            case "UDDecomposer":
                Console.WriteLine($"  check ud: HAS_POS={await RelationEvidence("HAS_POS", srcKey):N0} "
                                + $"IS_LEMMA_OF={await RelationEvidence("IS_LEMMA_OF", srcKey):N0}");
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
                                + $"HAS_NAME_ALIAS={await RelationEvidence("HAS_NAME_ALIAS", srcKey):N0} "
                                + $"IS_TYPED_AS={await RelationEvidence("IS_TYPED_AS", srcKey):N0}");
                break;
            case "FrameNetDecomposer":
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
