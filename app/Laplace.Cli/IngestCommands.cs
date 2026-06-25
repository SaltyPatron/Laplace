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
        bool RegisterOnly,
        bool Force = false);

    // Sources routed through the generic EtlDecomposer to prove parity with their bespoke
    // decomposer. The old decomposer switch stays in place for everything else (Migrate retires it).
    private static readonly HashSet<string> EtlGenericRouted =
        new(StringComparer.OrdinalIgnoreCase) { "omw", "conceptnet", "atomic2020", "wiktionary" };

    private static IngestCliArgs ParseIngestCliArgs(string[] args)
    {
        var rest = new List<string>(args);
        LanguageFilter? langs = null;
        bool? emitCross = null;
        bool skipEvidence = false;
        bool registerOnly = false;
        bool force = false;
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
            else i++;
        }
        return new(
            rest.Count > 0 ? rest[0] : "",
            rest.Count > 1 ? rest[1] : "",
            langs,
            emitCross,
            skipEvidence,
            registerOnly,
            force);
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
                        + "  language scope: --langs or LAPLACE_INGEST_LANGS; per-source LAPLACE_{SOURCE}_LANGS\n"
                        + "  --no-evidence: fold consensus only; skip laplace.attestations (or LAPLACE_PERSIST_EVIDENCE=0)");



        // NOTE: no process-wide CPU pin. The machine is shared (the user works on it; GPUs, hypervisor,
        // WSL all run too). Seizing every P-core logical thread starves the box. The OS hybrid scheduler
        // already keeps active CPU-bound work on P-cores when they're free and leaves headroom for the
        // user; worker counts (compose/decompose/commit) are the place to bound load, not affinity.
        CodepointPerfcache.Load(ResolveBlob());

        // Make the bespoke witnesses of the already-grammar-conforming sources available to the
        // generic EtlDecomposer (the parity oracle path); harmless for sources still on old classes.
        EtlWitnessRegistrations.RegisterAll();

        string sourceKey = cli.Source.ToLowerInvariant();

        // Manifest-driven generic path: a complete EtlManifest row drives ONE EtlDecomposer. Routed
        // for the already-conforming sources to prove parity; opt the rest in by completing their row.
        if (EtlGenericRouted.Contains(sourceKey) && EtlManifest.IsRoutable(sourceKey))
            return await IngestViaRunnerAsync(
                new EtlDecomposer(EtlManifest.Get(sourceKey)),
                IngestDataPaths.Resolve(sourceKey, cli.Path), skipLayerCheck: false, cli);

        return sourceKey switch
        {
            "unicode"  => await IngestUnicodeViaRunnerAsync(cli),
            "iso639"   => await IngestISO639Async(cli),
            "cili"     => await IngestViaRunnerAsync(new CILIDecomposer(), IngestDataPaths.Resolve("cili", cli.Path), skipLayerCheck: false, cli),
            "wordnet"  => await IngestViaRunnerAsync(new WordNetDecomposer(), IngestDataPaths.Resolve("wordnet", cli.Path), skipLayerCheck: false, cli),
            "omw"      => await IngestViaRunnerAsync(new OMWDecomposer(), IngestDataPaths.Resolve("omw", cli.Path), skipLayerCheck: false, cli),
            "ud"       => await IngestViaRunnerAsync(new UDDecomposer(), IngestDataPaths.Resolve("ud", cli.Path), skipLayerCheck: false, cli),
            "tatoeba"  => await IngestViaRunnerAsync(new TatoebaDecomposer(), IngestDataPaths.Resolve("tatoeba", cli.Path), skipLayerCheck: false, cli),
            "atomic2020" => await IngestViaRunnerAsync(new Atomic2020Decomposer(), IngestDataPaths.Resolve("atomic2020", cli.Path), skipLayerCheck: false, cli),
            "conceptnet" => await IngestViaRunnerAsync(new ConceptNetDecomposer(), IngestDataPaths.Resolve("conceptnet", cli.Path), skipLayerCheck: false, cli),
            "wiktionary" => await IngestViaRunnerAsync(new WiktionaryDecomposer(), IngestDataPaths.Resolve("wiktionary", cli.Path), skipLayerCheck: false, cli),
            "framenet" => await IngestViaRunnerAsync(new FrameNetDecomposer(), IngestDataPaths.Resolve("framenet", cli.Path), skipLayerCheck: false, cli),
            "opensubtitles" => await IngestViaRunnerAsync(new OpenSubtitlesDecomposer(), IngestDataPaths.Resolve("opensubtitles", cli.Path), skipLayerCheck: false, cli),
            "verbnet"  => await IngestViaRunnerAsync(new VerbNetDecomposer(),  IngestDataPaths.Resolve("verbnet", cli.Path),  skipLayerCheck: false, cli),
            "propbank" => await IngestViaRunnerAsync(new PropBankDecomposer(), IngestDataPaths.Resolve("propbank", cli.Path), skipLayerCheck: false, cli),
            "semlink"  => await IngestViaRunnerAsync(new SemLinkDecomposer(),  IngestDataPaths.Resolve("semlink", cli.Path),  skipLayerCheck: false, cli),
            "mapnet"   => await IngestViaRunnerAsync(new MapNetDecomposer(),   IngestDataPaths.Resolve("mapnet", cli.Path),   skipLayerCheck: false, cli),
            "wordframenet" => await IngestViaRunnerAsync(new WordFrameNetDecomposer(), IngestDataPaths.Resolve("wordframenet", cli.Path), skipLayerCheck: false, cli),
            "code"       => await IngestCodeAsync(cli),
            "repo"       => await IngestRepoAsync(cli),
            "tabular"    => await IngestTabularAsync(cli),
            "tiny-codes" => await IngestViaRunnerAsync(new TinyCodesDecomposer(),
                IngestDataPaths.Resolve("tiny-codes", cli.Path), skipLayerCheck: true, cli),
            "stack"      => await IngestViaRunnerAsync(new StackDecomposer(),
                IngestDataPaths.Resolve("stack", cli.Path), skipLayerCheck: true, cli),
            "model" or "safetensors" or "safetensor" => await IngestSafetensorSnapshotAsync(cli.Path, cli),
            "image"      => await IngestViaRunnerAsync(new ImageDecomposer(), IngestDataPaths.Resolve("image", cli.Path), skipLayerCheck: true, cli),
            "audio"      => await IngestViaRunnerAsync(new AudioDecomposer(), IngestDataPaths.Resolve("audio", cli.Path), skipLayerCheck: true, cli),
            "document"   => await IngestDocumentAsync(cli),
            "recipe"     => await IngestRecipeAsync(cli),
            "chess"      => await IngestViaRunnerAsync(
                new Laplace.Chess.Service.ChessPgnDecomposer(), cli.Path ?? "", skipLayerCheck: true, cli),
            _ => Fail($"unknown ingest source '{cli.Source}' (supported: unicode, iso639, wordnet, omw, ud, tatoeba, atomic2020, conceptnet, wiktionary, framenet, opensubtitles, verbnet, propbank, semlink, mapnet, wordframenet, code, repo, tabular, tiny-codes, stack, safetensors, image, audio, document, recipe)"),
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

    private static async Task<int> IngestRecipeAsync(IngestCliArgs cli)
    {
        if (string.IsNullOrEmpty(cli.Path))
            return Fail("usage: laplace ingest recipe <recipe.json>\n"
                        + "  Deposits a build-a-bear recipe (the simulated UI POST) as a content-addressed\n"
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

    private static async Task<int> IngestUnicodeViaRunnerAsync(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new UnicodeDecomposer(), IngestDataPaths.Resolve("unicode", cli.Path), skipLayerCheck: true, cli);

    private static async Task<int> IngestISO639Async(IngestCliArgs cli)
        => await IngestViaRunnerAsync(new ISODecomposer(), IngestDataPaths.Resolve("iso639", cli.Path), skipLayerCheck: false, cli);

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

    private static int ResolveCommitRows(string sourceName)
    {
        var raw = Environment.GetEnvironmentVariable("LAPLACE_INGEST_COMMIT_ROWS");
        if (int.TryParse(raw, out var env) && env >= 0)
            return env;
        // High-fanout grammar witnesses emit hundreds of thousands of substrate rows per
        // input line; keep commit threshold above typical intent size to batch DB applies.
        return sourceName switch
        {
            "ConceptNetDecomposer" => 4_000_000,
            _ => 250_000,
        };
    }

    private static IngestRunOptions BuildIngestOptions(
        Stopwatch sw, string sourceName, bool skipLayerCheck, string? ecosystemPath,
        IngestCliArgs? cli = null, bool skipSourceCompletion = false, bool bulkFresh = false)
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
        int workers = EnvInt("LAPLACE_INGEST_WORKERS", CpuTopology.ResolveIoBoundWorkers(defaultCap: 8), min: 1);
        long maxUnits = EnvLong("LAPLACE_INGEST_MAX_UNITS", 0, min: 0);
        int commitRows = ResolveCommitRows(sourceName);
        var decoOpts = DecomposerOptions.ForWitness(
            sourceName, batch, cli?.LangOverride, cli?.EmitCrossLanguageLinks);
        if (maxUnits > 0)
            decoOpts = decoOpts with { MaxInputUnits = maxUnits };
        return IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = skipLayerCheck,
            SkipSourceCompletion   = skipSourceCompletion,
            EcosystemPath          = ecosystemPath,
            BatchSize              = batch,
            DecomposerOptions      = decoOpts,
            CommitRows             = commitRows,
            ParallelWorkers        = workers,
            Progress               = progress,
            BulkFresh              = bulkFresh,
            
            
            
            
            
            RetryPolicy                = workers > 1
                                            ? TransientErrorRetryPolicy.ConcurrencyRetry
                                            : TransientErrorRetryPolicy.NoRetry,
            AbortOnTransientExhaustion = true,
        };
    }

    private static bool IsEnvEnabled(string name) =>
        Environment.GetEnvironmentVariable(name) is "1" or "true" or "True" or "yes" or "YES";

    private static async Task<int> IngestViaRunnerAsync(
        IDecomposer dec, string ecosystemPath, bool skipLayerCheck, IngestCliArgs? cli = null,
        bool skipSourceCompletion = false)
    {
        CodepointPerfcache.Load(ResolveBlob());

        LanguageReference.EnsureLoaded();

        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        var loggerFactory = ConsoleLoggerProvider.Factory();
        bool force = cli?.Force ?? false;
        bool bulkFresh = force || IsEnvEnabled("LAPLACE_BULK_FRESH");
        var innerWriter = new NpgsqlSubstrateWriter(ds, bulkFreshSource: bulkFresh);
        bool persistEvidence = ResolvePersistEvidence(cli);
        await using var accumulator = new ConsensusAccumulatingWriter(innerWriter, ds,
            freshSource: bulkFresh,
            persistEvidence: persistEvidence,
            stageAsWalks: !persistEvidence,
            logger: loggerFactory.CreateLogger<ConsensusAccumulatingWriter>());
        var writer = (ISubstrateWriter)accumulator;
        var reader = new NpgsqlSubstrateReader(ds);
        var runner = new IngestRunner(writer, reader, loggerFactory);





        var indexPolicy = new SecondaryIndexPolicy(ds, loggerFactory.CreateLogger<SecondaryIndexPolicy>());
        await using var physScope = await indexPolicy.SuspendForBulkLoadAsync("physicalities", CancellationToken.None);
        if (physScope.Dropped)
            Console.WriteLine($"B2: dropped {physScope.DroppedIndexDefs.Count} secondary physicalities index(es) "
                            + "(incl. coord GiST + trajectory GIN) for index-free bulk load (empty table); rebuilt after apply");
        else if (physScope.TableWasPopulated)
            Console.WriteLine("B2: physicalities populated — keeping indexes live (incremental maintenance; "
                            + "fresh-DB seeds drop once and rebuild after all decomposers)");

        Console.WriteLine($"ingest {dec.SourceName} via IngestRunner → {ConnString} ..."
            + (persistEvidence ? "" : " (consensus-only, no attestation writes)")
            + (bulkFresh ? " (bulk-fresh preflight skip)" : ""));
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            dec,
            BuildIngestOptions(sw, dec.SourceName, skipLayerCheck, ecosystemPath, cli,
                skipSourceCompletion || (cli?.Force ?? false), bulkFresh: bulkFresh),
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



        if (physScope.Dropped && !physScope.Rebuilt)
        {
            Console.WriteLine($"B2: rebuilding {physScope.DroppedIndexDefs.Count} secondary physicalities "
                            + "index(es) (coord GiST via Hilbert-packed bulk build) ...");
            var ixSw = Stopwatch.StartNew();
            await physScope.RebuildAsync(CancellationToken.None);
            ixSw.Stop();
            Console.WriteLine($"B2: secondary physicalities indexes rebuilt in {ixSw.Elapsed.TotalSeconds:F1}s");
        }

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

        // Validation is a post-fold diagnostic, not a hot path: on a fresh-DB seed the
        // counters/render/define helpers scan freshly-written tables with cold caches, which
        // can exceed Npgsql's 30s default and surface as "Exception while reading from stream".
        // Every command in this method runs without a timeout (CommandTimeout = 0).
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

        Console.WriteLine("substrate counts:");
        {
            await using var counts = Cmd();
            counts.CommandText = "SELECT metric, value FROM laplace.substrate_counts()";
            await using var rdr = await counts.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                Console.WriteLine($"  {rdr.GetString(0),-24}: {rdr.GetInt64(1),12:N0}");
        }

        if (decomposer is null)
        {
            Console.WriteLine("  witnesses:");
            await using var src = Cmd();
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
                // Geometry is source-free: the physicality for U+0041 is one row keyed by
                // (entity_id, type), no source. Provenance ("the Unicode source deposited this")
                // is counted via attestations, not via a physicality source filter.
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
