using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>
/// Measures authentic source games through the existing in-process PGN admission,
/// synchronous writer and exact native readback. Preparation does not generate games.
/// </summary>
public static class ChessCorpusBenchmark
{
    public sealed record Options(string PgnPath, string EvidenceDirectory, int Games = 75_000,
        double MinimumSeconds = 30, int Replays = 1, int DeadlineSeconds = 3600,
        string? ExpectedSha256 = null);
    public sealed record Result(bool Completed, int NewlyRecordedGames, double ElapsedSeconds,
        double GamesPerSecond, bool QualifiedWindow, bool TargetMet, string ReceiptPath);

    internal static void ValidateOptions(Options options)
    {
        if (!Path.IsPathFullyQualified(options.PgnPath) || !Path.IsPathFullyQualified(options.EvidenceDirectory)
            || !options.PgnPath.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(options.PgnPath) == Path.GetFullPath(options.EvidenceDirectory))
            throw new ArgumentException("corpus measurement requires distinct absolute PGN/evidence paths and plain .pgn input");
        if (options.Games is < 1 or > 1_000_000 || options.Replays is < 1 or > 4
            || options.DeadlineSeconds is < 30 or > 86400 || !double.IsFinite(options.MinimumSeconds)
            || options.MinimumSeconds is < 30 or > 3600 || options.MinimumSeconds > options.DeadlineSeconds)
            throw new ArgumentException("corpus measurement limits are outside their supported bounds");
        if (options.ExpectedSha256 is { } hash && (hash.Length != 64
            || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))))
            throw new ArgumentException("expected source SHA256 must be 64 lowercase hexadecimal characters");
    }

    internal sealed record ExecutionFailure(string Status, string Error, string ErrorType);

    // The guarded operation includes await-using disposal. A completed body does not
    // qualify a run if releasing its owned ingestor fails afterward.
    internal static async Task<ExecutionFailure?> CaptureFailureAsync(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (OperationCanceledException failure)
        {
            return new("cancelled", "corpus benchmark deadline or cancellation", failure.GetType().Name);
        }
        catch (Exception failure)
        {
            return new("failed", failure.Message, failure.GetType().Name);
        }
    }

    public static async Task<Result> RunAsync(Options options, CancellationToken ct = default)
    {
        ValidateOptions(options);
        options = options with { PgnPath = Path.GetFullPath(options.PgnPath),
            EvidenceDirectory = Path.GetFullPath(options.EvidenceDirectory) };
        if (!File.Exists(options.PgnPath)) throw new FileNotFoundException("source PGN is absent", options.PgnPath);
        if (Directory.Exists(options.EvidenceDirectory) || File.Exists(options.EvidenceDirectory))
            throw new IOException("corpus evidence directory must be new");
        Directory.CreateDirectory(options.EvidenceDirectory);
        string receiptPath = Path.Combine(options.EvidenceDirectory, "corpus-recording.json");
        // The exclusive marker prevents two callers sharing one evidence directory.
        await using (var marker = new FileStream(Path.Combine(options.EvidenceDirectory, "owner"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DeadlineSeconds));
        var token = deadline.Token;
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var phases = new List<ChessRecordingMeasurement>();
        ChessCorpusPreparation? preparation = null;
        ChessRecordingMeasurement? fresh = null;
        string status = "failed";
        string? error = null;
        string? errorType = null;
        double setupSeconds = 0, preparationSeconds = 0, admissionSeconds = 0;
        bool qualified = false, targetMet = false;
        double rate = 0;
        var failure = await CaptureFailureAsync(async () =>
        {
            long setupStart = Stopwatch.GetTimestamp();
            await using var ingestor = await ChessPgnIngestor.CreateAsync(token);
            setupSeconds = Stopwatch.GetElapsedTime(setupStart).TotalSeconds;
            long preparationStart = Stopwatch.GetTimestamp();
            try { preparation = await ChessCorpusPreparation.PrepareAsync(options, ingestor, token); }
            finally { preparationSeconds = Stopwatch.GetElapsedTime(preparationStart).TotalSeconds; }
            await WriteJsonAsync(Path.Combine(options.EvidenceDirectory, "preparation.json"), preparation);
            if (preparation.Selected.Count != options.Games)
                throw new InvalidDataException("source corpus has fewer eligible distinct fresh PLAYING occurrences than requested");

            async Task<ChessCorpusEvidence> AdmitAsync(string name, ChessCorpusEvidence? baseline = null)
            {
                string directory = Path.Combine(options.EvidenceDirectory, name);
                var evidence = new ChessCorpusEvidence(directory, baseline);
                var measurement = ChessRecordingMeasurement.FromCorpus(preparation, evidence);
                phases.Add(measurement);
                if (baseline is null) fresh = measurement;
                try
                {
                    await ingestor.IngestCorpusGamesAsync(preparation.ReadSelected(token), measurement, token);
                    await measurement.VerifyPgnUnchangedAsync(preparation.Source.Path, token);
                    await ChessCorpusPreparation.RequireUnchangedAsync(preparation.SelectionManifest, token);
                    await evidence.CompleteAsync(token);
                    measurement.Complete("completed");
                    return evidence;
                }
                catch (OperationCanceledException)
                {
                    measurement.Complete("cancelled", "corpus admission deadline or cancellation");
                    throw;
                }
                catch (Exception failure)
                {
                    measurement.Complete("failed", failure.Message);
                    throw;
                }
                finally { await measurement.WriteAsync(Path.Combine(directory, "recording.json")); }
            }

            ChessCorpusEvidence freshEvidence;
            long admissionStart = Stopwatch.GetTimestamp();
            try { freshEvidence = await AdmitAsync("fresh"); }
            finally { admissionSeconds = Stopwatch.GetElapsedTime(admissionStart).TotalSeconds; }
            if (fresh!.NovelGames != options.Games || fresh.AppliedGames != options.Games
                || fresh.ReadbackGames != options.Games || fresh.CommittedGames != options.Games
                || fresh.Writer.ApplyCalls < 1 || fresh.Durability is not { LocalWalFlushAcknowledged: true })
                throw new InvalidDataException("fresh capacity admission did not newly commit and exactly read back every selected playing");

            for (int replay = 1; replay <= options.Replays; replay++)
            {
                await AdmitAsync($"replay-{replay:D2}", freshEvidence);
                var measured = phases[^1];
                if (!measured.IsVerifiedNoOpReplay || !ChessCorpusEvidence.WriterIsZero(measured.Writer)
                    || measured.Durability is not null || measured.NovelGames != 0 || measured.AppliedGames != 0)
                    throw new InvalidDataException("corpus replay did not preserve zero writer work and exact scope");
            }
            rate = fresh.NovelGames / admissionSeconds;
            qualified = admissionSeconds >= options.MinimumSeconds && preparation.DistinctLines >= 2;
            targetMet = qualified && rate >= 2500;
        });
        // Completion is assigned only after every phase and ingestor disposal returned.
        status = failure?.Status ?? "completed";
        error = failure?.Error;
        errorType = failure?.ErrorType;
        if (failure is not null) { qualified = false; targetMet = false; }
        int confirmedNewlyRecorded = fresh?.CorpusEvidence?.NewlyRecordedGames ?? 0;
        {
            var receipt = new
            {
                schema = "laplace.chess-corpus-capacity/v1", status, error, errorType,
                startedAt, finishedAt = DateTimeOffset.UtcNow, options,
                elapsedSeconds = new { total = Stopwatch.GetElapsedTime(started).TotalSeconds,
                    setup = setupSeconds, preparation = preparationSeconds, freshAdmission = admissionSeconds },
                timingScope = "One contiguous warmed admission window: original source reading/framing, native parsing/normalization, ordinary shared composition and writer, synchronous WAL acknowledgement, complete exact native readback, per-chunk evidence and final exact scope manifest. Source selection/bootstrap occur before it; exact replay follows separately.",
                source = preparation,
                configuration = new { resolvedGamesPerChunk = ChessPgnIngestor.ResolvedGamesPerChunk,
                    logicalProcessors = Environment.ProcessorCount,
                    runtime = RuntimeInformation.FrameworkDescription,
                    operatingSystem = RuntimeInformation.OSDescription,
                    admissionLanes = 1, wholeMachineMaximumClaimed = false },
                newlyRecordedGames = confirmedNewlyRecorded,
                alreadyPresentGames = fresh?.CorpusEvidence is { } completedChunks
                    ? completedChunks.ReadbackGames - completedChunks.NewlyRecordedGames : 0,
                parsedGamesWithoutCompleteChunkEvidence = fresh is null ? 0
                    : fresh.ParsedGames - (fresh.CorpusEvidence?.ReadbackGames ?? 0),
                readbackGames = fresh?.ReadbackGames ?? 0,
                recordedGamesPerSecond = status == "completed" ? (double?)rate : null,
                qualifiedWindow = qualified, targetGamesPerSecond = 2500, targetMet,
                targetVerdict = status != "completed" ? "unqualified-failed"
                    : !qualified ? "unqualified-duration-or-content-variation"
                    : targetMet ? "established-for-this-workload" : "below-target-for-this-workload",
                workload = "Authentic unchanged complete source games including human resignations and agreed draws; no engine-generation throughput, invented occurrence headers or replay counted as novel. Prepared source/identity caches are warm.",
                phases,
            };
            await WriteJsonAsync(receiptPath, receipt);
        }
        return new(status == "completed", confirmedNewlyRecorded, admissionSeconds,
            status == "completed" ? rate : 0, qualified, targetMet, receiptPath);
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await File.WriteAllTextAsync(path + ".pending", JsonSerializer.Serialize(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(path + ".pending", path, overwrite: true);
    }
}
