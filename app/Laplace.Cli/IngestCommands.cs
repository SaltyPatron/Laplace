using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
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
using Laplace.Decomposers.Image;
using Laplace.Decomposers.Audio;
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
    private sealed record IngestCliArgs(
        string Source,
        string Path,
        LanguageFilter? LangOverride,
        bool? EmitCrossLanguageLinks,
        bool SkipEvidence,
        bool RegisterOnly);

    private static IngestCliArgs ParseIngestCliArgs(string[] args)
    {
        var rest = new List<string>(args);
        LanguageFilter? langs = null;
        bool? emitCross = null;
        bool skipEvidence = false;
        bool registerOnly = false;
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
            else i++;
        }
        return new(
            rest.Count > 0 ? rest[0] : "",
            rest.Count > 1 ? rest[1] : "",
            langs,
            emitCross,
            skipEvidence,
            registerOnly);
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
                        + "  sources: unicode | iso639 | wordnet | omw | ud | tatoeba | atomic2020 | conceptnet | wiktionary | framenet | opensubtitles | verbnet | propbank | semlink | code | repo | tabular | tiny-codes | stack | safetensors | image | audio | document\n"
                        + "  language scope: --langs or LAPLACE_INGEST_LANGS; per-source LAPLACE_{SOURCE}_LANGS\n"
                        + "  --no-evidence: fold consensus only; skip laplace.attestations (or LAPLACE_PERSIST_EVIDENCE=0)");

        // every ingest verb deposits content witnesses, and the native floor
        // requires the codepoint table — load it once here, not per-verb
        CodepointPerfcache.Load(ResolveBlob());

        return cli.Source.ToLowerInvariant() switch
        {
            "unicode"  => await IngestUnicodeViaRunnerAsync(cli),
            "iso639"   => await IngestISO639Async(cli),
            "wordnet"  => await IngestViaRunnerAsync(new WordNetDecomposer(), "/vault/Data/Wordnet", skipLayerCheck: false, cli),
            "omw"      => await IngestViaRunnerAsync(new OMWDecomposer(), "/vault/Data/omw", skipLayerCheck: false, cli),
            "ud"       => await IngestViaRunnerAsync(new UDDecomposer(), "/vault/Data/UD-Treebanks", skipLayerCheck: false, cli),
            "tatoeba"  => await IngestViaRunnerAsync(new TatoebaDecomposer(), "/vault/Data/Tatoeba", skipLayerCheck: false, cli),
            "atomic2020" => await IngestViaRunnerAsync(new Atomic2020Decomposer(), "/vault/Data/Atomic2020", skipLayerCheck: false, cli),
            "conceptnet" => await IngestViaRunnerAsync(new ConceptNetDecomposer(), "/vault/Data/ConceptNet", skipLayerCheck: false, cli),
            "wiktionary" => await IngestViaRunnerAsync(new WiktionaryDecomposer(), "/vault/Data/Wiktionary", skipLayerCheck: false, cli),
            "framenet" => await IngestViaRunnerAsync(new FrameNetDecomposer(), "/vault/Data/FrameNet/framenet_v17", skipLayerCheck: false, cli),
            "opensubtitles" => await IngestViaRunnerAsync(new OpenSubtitlesDecomposer(), "/vault/Data/OpenSubtitles", skipLayerCheck: false, cli),
            "verbnet"  => await IngestViaRunnerAsync(new VerbNetDecomposer(),  "/vault/Data/VerbNet",  skipLayerCheck: false, cli),
            "propbank" => await IngestViaRunnerAsync(new PropBankDecomposer(), "/vault/Data/PropBank", skipLayerCheck: false, cli),
            "semlink"  => await IngestViaRunnerAsync(new SemLinkDecomposer(),  "/vault/Data/SemLink",  skipLayerCheck: false, cli),
            "code"       => await IngestCodeAsync(cli),
            "repo"       => await IngestRepoAsync(cli),
            "tabular"    => await IngestTabularAsync(cli),
            "tiny-codes" => await IngestViaRunnerAsync(new TinyCodesDecomposer(),
                ResolveIngestPath(cli.Path, "/vault/Data/tiny-codes"), skipLayerCheck: true, cli),
            "stack"      => await IngestViaRunnerAsync(new StackDecomposer(),
                ResolveIngestPath(cli.Path, "/vault/Data/stack-v2"), skipLayerCheck: true, cli),
            "model" or "safetensors" or "safetensor" => await IngestSafetensorSnapshotAsync(cli.Path, cli),
            "image"      => await IngestViaRunnerAsync(new ImageDecomposer(), string.IsNullOrEmpty(cli.Path) ? "/vault/Data/test-data/images" : cli.Path, skipLayerCheck: true, cli),
            "audio"      => await IngestViaRunnerAsync(new AudioDecomposer(), string.IsNullOrEmpty(cli.Path) ? "/vault/Data/test-data/audio" : cli.Path, skipLayerCheck: true, cli),
            "document"   => await IngestDocumentAsync(cli),
            _ => Fail($"unknown ingest source '{cli.Source}' (supported: unicode, iso639, wordnet, omw, ud, tatoeba, atomic2020, conceptnet, wiktionary, framenet, opensubtitles, verbnet, propbank, semlink, code, repo, tabular, tiny-codes, stack, safetensors, image, audio, document)"),
        };
    }

    private static async Task<int> IngestSafetensorSnapshotAsync(string modelDir, IngestCliArgs cli)
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

        // Bulk deposit: a multi-hour COPY stream into the walk journal must never
        // hit the 30 s default command timeout — that timeout governs each COPY
        // write-buffer flush, and a large COMPLETES_TO flush under disk pressure
        // exceeds it (the TinyLlama walk smoke died there, 2026-06-12). The fold
        // already sets CommandTimeout=0 per command; the COPY stream has no command
        // object, so it inherits the connection default — disable it at the source.
        var dsb = new NpgsqlDataSourceBuilder(ConnString);
        dsb.ConnectionStringBuilder.CommandTimeout = 0;
        await using var ds = dsb.Build();

        var dec = new ModelDecomposer(modelDir, persistEvidence: ResolvePersistEvidence(cli));

        // Repair lane: re-register readback canonicals (recipe JSON + scalars)
        // for a deposit whose post-steps died (e.g. a fold ENOSPC) without
        // re-walking the tensors. Idempotent.
        if (cli.RegisterOnly)
        {
            await RegisterDynamicCanonicalsAsync(ds, dec);
            return 0;
        }

        var (modelSource, modelName) = ModelDecomposer.SourceForModel(modelDir);
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
            logger: loggerFactory.CreateLogger<NpgsqlSubstrateWriter>(),
            bulkFreshSource: true);
        // The behavioral token planes MERGE: the same (token, ATTENDS, token) pair
        // legitimately recurs across layers and accumulation windows, and cross-layer
        // agreement is the strongest testimony — a FRESH fold would silently drop it.
        // Without evidence the ETL emits testimony walks, and the writer must journal
        // ALL consensus partials (vocab, S3-morph) as walks too — one shape, one fold.
        bool persistEvidenceResolved = ResolvePersistEvidence(cli);
        var accumulator = new ConsensusAccumulatingWriter(inner, ds,
            freshSource: false,
            persistEvidence: persistEvidenceResolved,
            stageAsWalks: !persistEvidenceResolved,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        ISubstrateWriter writer = accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);
        Console.WriteLine("mode: bulk fresh-source apply (attestation existence check skipped — safetensor snapshot is uningested); consensus accumulates at ingest");

        Console.WriteLine($"deposit safetensor snapshot {modelDir} via IngestRunner → {ConnString} ...");

        // Index-free bulk load is correct ONLY when seeding an empty table; the drop/keep decision
        // and the structural rebuild-on-exit guarantee live in SecondaryIndexPolicy (SubstrateCRUD).
        var indexPolicy = new SecondaryIndexPolicy(ds, loggerFactory.CreateLogger<SecondaryIndexPolicy>());
        await using var attScope = await indexPolicy.SuspendForBulkLoadAsync("attestations", CancellationToken.None);
        if (attScope.Dropped)
            Console.WriteLine($"B2: dropped {attScope.DroppedIndexDefs.Count} secondary attestations index(es) for index-free bulk load (empty table); rebuilt after apply");
        else if (attScope.TableWasPopulated)
            Console.WriteLine("B2: attestations populated — keeping indexes live; bounded model load maintains them incrementally (no whole-table rebuild)");

        await using var consScope = await indexPolicy.SuspendForBulkLoadAsync("consensus", CancellationToken.None);
        if (consScope.Dropped)
            Console.WriteLine($"B2: dropped {consScope.DroppedIndexDefs.Count} secondary consensus index(es) for index-free fold (empty table); rebuilt after the consensus fold");
        else if (consScope.TableWasPopulated)
            Console.WriteLine("B2: consensus populated — keeping indexes live; bounded model fold maintains them incrementally (no whole-table rebuild)");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await runner.RunAsync(
                dec,
                BuildIngestOptions(sw, dec.SourceName, skipLayerCheck: true, ecosystemPath: null, cli)
                with { DecomposerOptions = DecomposerOptions.ForWitness(
                    dec.SourceName,
                    EnvInt("LAPLACE_INGEST_BATCH", 2048, min: 1),
                    cli.LangOverride,
                    cli.EmitCrossLanguageLinks) },
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

            // Register BEFORE the fold: readback canonicals (the recipe JSON the
            // foundry's --recipe-from reads back) depend only on the applied
            // deposit, and the fold is the long, failure-prone tail — a fold
            // ENOSPC once ate the recipe registration of a completed deposit.
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
            // Both scopes dropped their indexes (if at all) BEFORE the run started, so they must be
            // rebuilt here regardless of how the run/fold above exits — success, failure rows, or a
            // thrown exception. The scopes' DisposeAsync is the structural safety net; rebuilding
            // explicitly here narrates the timing. Rebuilding only on the happy path is what stranded
            // `consensus` index-free in production: a failed/throwing model ingest left it with only
            // its primary key, forcing recall_session/recall/neighbors into seq-scans over 57M rows.
            sw.Stop();
            if (attScope.Dropped && !attScope.Rebuilt)
            {
                Console.WriteLine($"B2: rebuilding {attScope.DroppedIndexDefs.Count} secondary attestations index(es) ...");
                var ixSw = Stopwatch.StartNew();
                await attScope.RebuildAsync(CancellationToken.None);
                ixSw.Stop();
                Console.WriteLine($"B2: secondary attestations indexes rebuilt in {ixSw.Elapsed.TotalSeconds:F1}s");
            }
            if (consScope.Dropped && !consScope.Rebuilt)
            {
                Console.WriteLine($"B2: rebuilding {consScope.DroppedIndexDefs.Count} secondary consensus index(es) ...");
                var cixSw = Stopwatch.StartNew();
                await consScope.RebuildAsync(CancellationToken.None);
                cixSw.Stop();
                Console.WriteLine($"B2: secondary consensus indexes rebuilt in {cixSw.Elapsed.TotalSeconds:F1}s");
            }
        }
        try { await PrintIngestValidationAsync(ds, dec); }
        catch (Exception ex)
        { Console.Error.WriteLine($"warn: safetensor deposition validation failed: {ex.Message}"); }
        return 0;
    }

    private static async Task<int> IngestDocumentAsync(IngestCliArgs cli)
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

    private static async Task<int> IngestUnicodeViaRunnerAsync(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new UnicodeDecomposer(), "/vault/Data/UCD/Public/UCD/latest", skipLayerCheck: true, cli);

    private static async Task<int> IngestISO639Async(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new ISODecomposer(), "/vault/Data/ISO639", skipLayerCheck: false, cli);

    private static string ResolveIngestPath(string? cliPath, string defaultPath)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(cliPath) ? defaultPath : cliPath);

    private static string? ResolveRequiredIngestPath(string? cliPath)
        => string.IsNullOrWhiteSpace(cliPath) ? null : Path.GetFullPath(cliPath);

    private static async Task<int> IngestCodeAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest code <file-or-directory>");
        return await IngestViaRunnerAsync(new CodeDecomposer(), path, skipLayerCheck: true, cli);
    }

    private static async Task<int> IngestRepoAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest repo <repository-root>");
        return await IngestViaRunnerAsync(new RepoDecomposer(), path, skipLayerCheck: true, cli);
    }

    private static async Task<int> IngestTabularAsync(IngestCliArgs cli)
    {
        var path = ResolveRequiredIngestPath(cli.Path);
        if (path is null)
            return Fail("usage: laplace ingest tabular <file-or-directory>");
        return await IngestViaRunnerAsync(new TabularDecomposer(), path, skipLayerCheck: true, cli);
    }

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath,
        IngestCliArgs? cli = null, bool skipSourceCompletion = false)
    {
        long lastMs = -10_000;
        var progress = new Progress<Laplace.Ingestion.IngestProgress>(p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastMs < 2000) return;
            lastMs = now;
            double secs = Math.Max(0.001, p.Elapsed.TotalSeconds);
            long rowsNew = p.EntitiesInserted + p.PhysicalitiesInserted + p.AttestationsInserted;
            string filePart = p.FilesTotal > 0 ? $"files={p.FilesDone}/{p.FilesTotal} file_pct={p.FilePercent:F1}" : "";
            string inputPart = p.InputUnitsTotal > 0
                ? $"input={p.InputUnitsDone}/{p.InputUnitsTotal} input_pct={p.InputPercent:F1}"
                : $"intents={p.UnitsApplied}/{p.UnitsProduced} intent_pct={p.InputPercent:F1}";
            string cur = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" current={p.CurrentFile}";
            Console.Error.WriteLine(
                $"INGEST_PROGRESS source={p.SourceName} layer={p.LayerOrder} unit_type={p.UnitType} "
                + $"{inputPart} {filePart}{cur} "
                + $"rows_new={rowsNew:N0} rate_rows_s={rowsNew / secs:N0} round_trips={p.RoundTrips:N0} "
                + $"elapsed_s={p.Elapsed.TotalSeconds:F0}"
                + (p.UnitsFailed > 0 ? $" failed={p.UnitsFailed:N0} status=failed" : " status=running"));
        });
        int batch = EnvInt("LAPLACE_INGEST_BATCH", 2048, min: 1);
        int workers = EnvInt("LAPLACE_INGEST_WORKERS", 1, min: 1);
        return IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = skipLayerCheck,
            SkipSourceCompletion   = skipSourceCompletion,
            EcosystemPath          = ecosystemPath,
            BatchSize              = batch,
            DecomposerOptions      = DecomposerOptions.ForWitness(
                sourceName, batch, cli?.LangOverride, cli?.EmitCrossLanguageLinks),
            CommitRows             = EnvInt("LAPLACE_INGEST_COMMIT_ROWS", 250_000, min: 0),
            ParallelWorkers        = workers,
            Progress               = progress,
            // Serial ingest fails fast — no retry/backoff, no silent transient drop: the first
            // error surfaces and aborts so the real cause is fixed, never retried or swallowed.
            // Parallel ingest (workers>1) instead retries the genuine concurrency outcomes
            // (40P01 deadlock / 40001 serialization) the way Postgres prescribes — the victim
            // re-runs; everything else still fails fast. A persistent fault aborts after the cap.
            RetryPolicy                = workers > 1
                                            ? TransientErrorRetryPolicy.ConcurrencyRetry
                                            : TransientErrorRetryPolicy.NoRetry,
            AbortOnTransientExhaustion = true,
        };
    }

    private static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        CodepointPerfcache.Load(ResolveBlob());

        LanguageReference.EnsureLoaded();

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var loggerFactory = ConsoleLoggerProvider.Factory();
        var innerWriter = new NpgsqlSubstrateWriter(ds);
        bool persistEvidence = ResolvePersistEvidence(cli);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            persistEvidence: persistEvidence,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var writer = (ISubstrateWriter)accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)"));
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli, skipSourceCompletion),
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
        // Before the fold: registration depends only on the applied deposit,
        // and the fold is the failure-prone tail (see safetensors path).
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

        async Task<long> EvidenceForSource(string sourceKey)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT laplace.evidence_count(p_source => laplace.source_id($1))";
            c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<long> ContentForSource(string sourceKey)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT laplace.content_count(p_source => laplace.source_id($1))";
            c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<long> RelationEvidence(string relationType, string? sourceKey = null)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = sourceKey is null
                ? "SELECT laplace.evidence_count(p_type => laplace.relation_type_id($1))"
                : "SELECT laplace.evidence_count(p_type => laplace.relation_type_id($1), p_source => laplace.source_id($2))";
            c.Parameters.AddWithValue(relationType);
            if (sourceKey is not null) c.Parameters.AddWithValue(sourceKey);
            return (long)(await c.ExecuteScalarAsync() ?? 0L);
        }

        async Task<bool> LayerMarkedComplete(int layer, string sourceKey)
        {
            await using var c = conn.CreateCommand();
            c.CommandText =
                "SELECT laplace.evidence_count("
                + "p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/' || $1 || '/v1'), "
                + "p_source => laplace.source_id($2)) > 0";
            c.Parameters.AddWithValue(layer);
            c.Parameters.AddWithValue(sourceKey);
            return (bool)(await c.ExecuteScalarAsync() ?? false);
        }

        Console.WriteLine("substrate counts:");
        {
            await using var counts = conn.CreateCommand();
            counts.CommandTimeout = 0;
            counts.CommandText = "SELECT metric, value FROM laplace.substrate_counts()";
            await using var rdr = await counts.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                Console.WriteLine($"  {rdr.GetString(0),-24}: {rdr.GetInt64(1),12:N0}");
        }

        if (decomposer is null)
        {
            Console.WriteLine("  witnesses:");
            await using var src = conn.CreateCommand();
            src.CommandTimeout = 0;   // exact per-source counts scan 126M rows (~minute); diagnostic, never cap it
            src.CommandText = "SELECT source, evidence, content FROM laplace.source_counts()";
            await using var rdr = await src.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                Console.WriteLine($"    {rdr.GetString(0),-28}: {rdr.GetInt64(1),12:N0} att  {rdr.GetInt64(2),12:N0} content");
            return;
        }

        string srcKey = decomposer.SourceName;
        long att = await EvidenceForSource(srcKey);
        long content = await ContentForSource(srcKey);
        bool layerOk = await LayerMarkedComplete(decomposer.LayerOrder, srcKey);
        Console.WriteLine($"  witness [{srcKey}] L{decomposer.LayerOrder}: {att:N0} attestations, {content:N0} content, layer_complete={layerOk}");

        if (decomposer.LayerOrder == 10)
        {
            string[] tensorRoles =
            [
                "EMBEDS", "Q_PROJECTS", "K_PROJECTS", "V_PROJECTS", "O_PROJECTS",
                "GATES", "UP_PROJECTS", "DOWN_PROJECTS", "NORM_SCALES", "OUTPUT_PROJECTS",
            ];
            long roleAtts = 0;
            foreach (var k in tensorRoles)
                roleAtts += await RelationEvidence(k, srcKey);
            Console.WriteLine($"  check safetensor deposition: {roleAtts:N0} tensor-role attestations "
                            + $"(snapshot witness, trust=AIModelProbe)");
            return;
        }

        switch (srcKey)
        {
            case "UnicodeDecomposer":
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT laplace.render(laplace.canonical_id('A')), f.tier,
                                           p.x, p.y, p.z, p.m, encode(p.hilbert_index, 'hex')
                                    FROM laplace.entity_facets(laplace.canonical_id('A')) f
                                    CROSS JOIN laplace.entity_physicalities(laplace.canonical_id('A')) p
                                    WHERE p.type = 1
                                      AND p.source_id = laplace.source_id('UnicodeDecomposer')";
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    Console.WriteLine("  check U+0041 'A':");
                    Console.WriteLine($"    render  : {rdr.GetString(0)}  tier={rdr.GetInt16(1)}");
                    Console.WriteLine($"    coord   : ({rdr.GetDouble(2):F6}, {rdr.GetDouble(3):F6}, {rdr.GetDouble(4):F6}, {rdr.GetDouble(5):F6})");
                }
                else Console.WriteLine("  FAIL: no Unicode CONTENT for U+0041");
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
                await using var cmd = conn.CreateCommand();
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
                                + $"HAS_LEMMA={await RelationEvidence("HAS_LEMMA", srcKey):N0}");
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
            case "FrameNetDecomposer":
                Console.WriteLine($"  check framenet: HAS_FRAME_ELEMENT={await RelationEvidence("HAS_FRAME_ELEMENT", srcKey):N0}");
                break;
            case "SemLinkDecomposer":
                Console.WriteLine($"  check semlink: CORRESPONDS_TO={await RelationEvidence("CORRESPONDS_TO", srcKey):N0}");
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
