using System.Diagnostics;
using System.Text.Json;

namespace Laplace.Chess.Service;

public sealed record ChessLabArtifactIngestResult(string Path, int Parsed, int Ingested,
    int AlreadyPresent, string? MeasurementArtifact, string Disposition);

public sealed partial class ChessLabService
{
    internal const int MaxRetainedIngestionReceipts = 32;
    /// <summary>The ordinary retained-PGN route uses the host-owned writer and the
    /// same common ingestion/readback loop as fresh matches. Each invocation has its
    /// own transport receipt; the original experiment is never rewritten on replay.</summary>
    public async Task<ChessLabArtifactIngestResult?> IngestArtifactAsync(string jobId, CancellationToken ct = default)
    {
        long serviceStarted = Stopwatch.GetTimestamp();
        if (!_jobs.TryGetValue(jobId, out var slot)) return null;
        lock (slot.Gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || !ReferenceEquals(slot, current)
                || !IsTerminal(slot.Job.State)) return null;
            slot.ActiveIngests++;
        }
        bool gateEntered = false;
        ChessRecordingMeasurement? measurement = null;
        string? artifact = null;
        string disposition = "failed";
        try
        {
            await slot.IngestGate.WaitAsync(ct);
            gateEntered = true;
            var job = Snapshot(slot);
            if (!job.Artifacts.TryGetValue("games.pgn", out var path) || !File.Exists(path)) return null;
            string? receiptPath = job.Artifacts.GetValueOrDefault("experiment.json");
            string? experimentJson = receiptPath is not null && File.Exists(receiptPath)
                ? await File.ReadAllTextAsync(receiptPath, ct) : null;
            if (job.Kind == ChessLabJobKind.Cutechess && job.State == ChessLabJobState.Completed && experimentJson is not null)
            {
                measurement = ChessRecordingMeasurement.FromRetainedMatch(job.Id, experimentJson);
                artifact = "ingest-" + Guid.NewGuid().ToString("N") + ".json";
                await measurement.IdentifyPgnAsync(path, experimentJson, ct);
            }
            var host = await GetLiveHostAsync(ct);
            await using var ingestor = await ChessPgnIngestor.AttachAsync(host, ct);
            var result = measurement is null
                ? await ingestor.IngestFileAsync(path, null, ct, experimentJson)
                : await ingestor.IngestRecordedFileAsync(path, measurement, null, ct, experimentJson!);
            disposition = result.Novel == result.Parsed ? "fresh"
                : result.Novel > 0 ? "mixed"
                : measurement?.IsVerifiedNoOpReplay == true ? "replay"
                : measurement is not null ? "repaired" : "existing-or-repaired";
            if (measurement is not null)
            {
                await measurement.VerifyPgnUnchangedAsync(path, ct);
                await measurement.IdentifyFinalExperimentAsync(receiptPath!);
                // Exact bytes remain bound even if another process changes the retained
                // experiment file while the normal ingestion operation is running.
                if (!string.Equals(await File.ReadAllTextAsync(receiptPath!, ct), experimentJson, StringComparison.Ordinal))
                    throw new InvalidDataException("retained experiment changed during ingestion/readback");
                measurement.Complete("completed");
            }
            return new(path, result.Parsed, result.Applied, result.Parsed - result.Novel, artifact, disposition);
        }
        catch (OperationCanceledException)
        {
            measurement?.Complete("cancelled", "cancelled");
            throw;
        }
        catch (Exception error)
        {
            measurement?.Complete("failed", error.Message);
            throw;
        }
        finally
        {
            try
            {
                if (measurement is not null && artifact is not null)
                {
                    string target = Path.Combine(_labDir, jobId, artifact);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var receipt = new
                    {
                        schema = "laplace.chess-retained-ingestion/v1",
                        jobId, disposition,
                        serviceElapsedSeconds = Stopwatch.GetElapsedTime(serviceStarted).TotalSeconds,
                        serviceElapsedScope = "HTTP service operation through attach/bootstrap/wait, parse, shared apply and exact readback; excludes receipt file serialization and HTTP response transport",
                        newlyRecordedGames = measurement.Status == "completed" ? (int?)measurement.NovelGames : null,
                        alreadyPresentGames = measurement.Status == "completed" ? (int?)(measurement.ParsedGames - measurement.NovelGames) : null,
                        noOpReplayVerified = measurement.IsVerifiedNoOpReplay && measurement.Status == "completed",
                        recording = measurement,
                    };
                    await File.WriteAllTextAsync(target + ".pending", JsonSerializer.Serialize(receipt,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    File.Move(target + ".pending", target, overwrite: false);
                    AddArtifact(slot, artifact, target);
                    PruneIngestionReceipts(slot);
                }
            }
            finally
            {
                if (gateEntered) slot.IngestGate.Release();
                lock (slot.Gate) slot.ActiveIngests--;
            }
        }
    }

    private void PruneIngestionReceipts(JobSlot slot)
    {
        lock (slot.Gate)
        {
            var artifacts = new Dictionary<string, string>(slot.Job.Artifacts, StringComparer.OrdinalIgnoreCase);
            var prior = artifacts.Where(a => a.Key.StartsWith("ingest-", StringComparison.Ordinal)
                    && a.Key.EndsWith(".json", StringComparison.Ordinal) && a.Key.Length == 44
                    && Guid.TryParseExact(a.Key[7..^5], "N", out _)
                    && Path.GetFullPath(a.Value) == Path.Combine(_labDir, slot.Job.Id, a.Key))
                .OrderByDescending(a => File.GetLastWriteTimeUtc(a.Value)).ThenBy(a => a.Key, StringComparer.Ordinal)
                .Skip(MaxRetainedIngestionReceipts).ToArray();
            foreach (var old in prior)
            {
                File.Delete(old.Value);
                artifacts.Remove(old.Key);
            }
            slot.Job = slot.Job with { Artifacts = artifacts };
        }
    }
}
