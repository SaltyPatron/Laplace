using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    internal static async Task<int> IngestNativeRecipeAsync(string source, IngestCliArgs cli, CancellationToken ct = default)
    {
        if (cli.LangOverride is not null || LanguageFilter.ForSource(source)?.IsActive == true)
            throw new ArgumentException("Native source ingestion admits the languages declared by its recipe. Remove --langs and ambient language filters, or select a language-scoped recipe.");
        if (cli.EmitCrossLanguageLinks is not null
            || cli.RegisterOnly || cli.Force || cli.NoAnalyze || cli.Recursive
            || cli.AnalyzeDepth != 0 || cli.AnalyzeNodes != 0 || cli.SecondPath.Length != 0
            || cli.GitCorpusSelection is not null || cli.GitCorpusReceipt is not null)
            throw new ArgumentException("Canonical source ingestion uses its native recipe. Select input paths or a recipe preview mode.");

        var execution = NativeRecipeExecution.Configured();
        string[] paths = string.IsNullOrWhiteSpace(cli.Path) ? [] : [cli.Path];
        var request = new NativeRecipeRequest(cli.NativeMode, source, paths,
            ConnectionInfo: NativeConnectionInfo(CliRuntime.ConnString), Threads: CpuTopology.ResolveCpuBoundWorkers(),
            ExpandPrerequisites: cli.NativeExpandPrerequisites);
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        using (var cancellation = new IngestCancellationScope(ct))
        {
            if (cli.NativeMode != NativeRecipeMode.Ingest)
                return await execution.ExecuteAsync(request, output, error, cancellation.Token).ConfigureAwait(false);

            await using var data = LaplaceDataSource.Create(SubstrateAccess.Ingest,
                b => b.ConnectionStringBuilder.CommandTimeout = 0, CliRuntime.ConnString);
            var journal = new NpgsqlIngestObservability(data,
                logger: CliRuntime.LoggerFactory.CreateLogger<NpgsqlIngestObservability>());
            // Native admission follows the recipe dependency DAG. Managed decomposer
            // layer numbers are a different contract and must not initialize its rows.
            const int layer = -1;
            var catalog = EtlManifest.Catalog;
            string canonicalSource = catalog.Resolve(source);
            journal.OnNativeRunStart(source, layer);
            var elapsed = Stopwatch.StartNew();
            long entities = 0, physicalities = 0, attestations = 0;
            long completedEntities = 0, completedPhysicalities = 0, completedAttestations = 0;
            bool completedAttestationsMeasured = true, attestationsMeasured = false;
            int total = 0, done = 0, completedFiles = 0;
            bool complete = false;
            Hash128 sourceId = default;
            double printedAt = -5;
            IngestProgress? lastProgress = null;
            void Observe(NativeRecipeProgress progress)
            {
                if (progress.Event == "inventory")
                {
                    total += progress.FilesTotal;
                    journal.OnNativeInventory(total);
                    complete = false;
                }
                entities = completedEntities + progress.Entities;
                physicalities = completedPhysicalities + progress.Physicalities;
                attestations = completedAttestations + (progress.Attestations ?? 0);
                done = completedFiles + progress.FilesDone;
                if (progress.File is { } file)
                {
                    if (progress.Event is "file-start" or "file-known")
                    {
                        var info = new FileInfo(file);
                        journal.OnFileStarted(source, file, info.Exists ? info.Length : progress.FileBytes);
                    }
                    if (progress.Event is "file-composed" or "file-known") journal.OnFileComposed(source, file,
                        progress.FileId is { } id ? Hash128.FromBytes(Convert.FromHexString(id)) : null,
                        records: progress.Records);
                }
                if (progress.Event == "complete")
                {
                    completedEntities = entities; completedPhysicalities = physicalities; completedFiles = done;
                    completedAttestations = attestations;
                    completedAttestationsMeasured &= progress.AttestationsMeasured == true;
                    if (progress.SourceId is { } id && catalog.Resolve(progress.Source ?? "") == canonicalSource)
                    {
                        sourceId = Hash128.FromBytes(Convert.FromHexString(id));
                        journal.OnNativeSourceIdentified(sourceId);
                    }
                    complete = catalog.Resolve(progress.Source ?? "") == canonicalSource;
                }
                bool measured = completedAttestationsMeasured && progress.AttestationsMeasured == true;
                if (measured != attestationsMeasured)
                {
                    journal.OnNativeAttestationMeasurement(measured);
                    attestationsMeasured = measured;
                }
                lastProgress = new IngestProgress(source, layer, 0, 0, 0,
                    total, done, total, done, progress.File, "files", elapsed.Elapsed,
                    EntitiesInserted: entities, PhysicalitiesInserted: physicalities, AttestationsInserted: attestations);
                journal.OnNativeProgress(lastProgress);
                if (elapsed.Elapsed.TotalSeconds - printedAt >= 5 || progress.Event == "complete")
                {
                    string reading = progress.File is null ? "" : $"; file={System.Text.Json.JsonSerializer.Serialize(progress.File)}; records={progress.Records:N0}";
                    string assertions = attestationsMeasured ? $", {attestations:N0} new attestations" : "";
                    Console.WriteLine($"ingest {source}: {done}/{total} files, {entities:N0} new entities, {physicalities:N0} new physicalities{assertions}; {progress.Event}{reading}");
                    printedAt = elapsed.Elapsed.TotalSeconds;
                }
            }
            try
            {
                if (journal.FailureCount != 0) throw new InvalidOperationException("The ingest run journal could not start.");
                int code = await execution.ExecuteAsync(request, output, error, cancellation.Token, Observe).ConfigureAwait(false);
                if (code != 0)
                {
                    if (lastProgress is { } final) journal.OnNativeProgress(final, force: true);
                    journal.OnRunFailed(source, "failed", $"Native ingestion exited with status {code}.");
                    journal.OnNativeFilesFinished("failed");
                    return code;
                }
                if (!complete || total == 0) throw new InvalidDataException("The Native Engine did not report a completed ingest with selected files for the requested source.");
                if (paths.Length == 0 && sourceId == default)
                    throw new InvalidDataException("The Native Engine reported files without publishing the requested corpus witness. The retained files do not establish completed corpus admission.");
                journal.OnRunFinished(source, new IngestRunResult(sourceId, source, 0, 0, 0,
                    entities, physicalities, attestations, 0, elapsed.Elapsed, [],
                    FilesDone: done, InputUnitsDone: done, InputUnitsTotal: total), "ok");
                journal.OnNativeFilesFinished("ok");
                if (journal.FailureCount != 0) throw new InvalidOperationException("Native ingestion completed, but its run/file receipts could not be persisted.");
                return 0;
            }
            catch (Exception ex)
            {
                string status = ex is OperationCanceledException ? "cancelled" : "failed";
                if (lastProgress is { } final) journal.OnNativeProgress(final, force: true);
                journal.OnRunFailed(source, status, ex.Message);
                journal.OnNativeFilesFinished(status);
                throw;
            }
        }
    }

    internal static string NativeConnectionInfo(string connectionString)
        => NativeRecipeExecution.ConnectionInfoFromNpgsql(connectionString);
}
