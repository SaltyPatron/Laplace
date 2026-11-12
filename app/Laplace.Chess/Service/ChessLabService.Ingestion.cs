using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

public sealed record ChessLabArtifactIngestResult(string Path, int Parsed, int Ingested,
    int AlreadyPresent, string? MeasurementArtifact, string Disposition);

public sealed partial class ChessLabService
{
    internal const int MaxRetainedIngestionReceipts = 32;
    /// <summary>
    /// A finished job's retained games.pgn, read by the Engine: a match's games, stated by the witness
    /// Laplace (the games Laplace played). Each invocation has its own receipt; the original experiment is never
    /// rewritten. Reading a file again finds its games already recorded.
    /// </summary>
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
        string? artifact = null;
        string? pgnPath = null;
        string? receiptPath = null;
        string? experimentTextSha256 = null;
        string status = "failed";
        string? failure = null;
        string disposition = "failed";
        int games = 0;
        try
        {
            await slot.IngestGate.WaitAsync(ct);
            gateEntered = true;
            var job = Snapshot(slot);
            if (!job.Artifacts.TryGetValue("games.pgn", out var path) || !File.Exists(path)) return null;
            pgnPath = path;
            receiptPath = job.Artifacts.GetValueOrDefault("experiment.json");
            if (receiptPath is not null && File.Exists(receiptPath))
            {
                artifact = "ingest-" + Guid.NewGuid().ToString("N") + ".json";
                experimentTextSha256 = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(receiptPath, ct))));
            }
            games = PgnGames.StreamGames(path).Count();
            var host = await GetLiveHostAsync(ct);
            await host.IngestFileAsync(ChessGameRecorder.Witness, path, ct);
            disposition = "recorded";
            status = "completed";
            return new(path, games, games, 0, artifact, disposition);
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            failure = "cancelled";
            throw;
        }
        catch (Exception error)
        {
            failure = error.Message;
            throw;
        }
        finally
        {
            try
            {
                if (artifact is not null)
                {
                    string target = Path.Combine(_labDir, jobId, artifact);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var receipt = new
                    {
                        schema = "laplace.chess-retained-ingestion/v2",
                        jobId, disposition, status, error = failure, requester = slot.Job.Requester,
                        witness = ChessGameRecorder.Witness, games,
                        inputs = new { pgnPath, experimentPath = receiptPath, experimentTextSha256 },
                        serviceElapsedSeconds = Stopwatch.GetElapsedTime(serviceStarted).TotalSeconds,
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
