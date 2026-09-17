using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Verifies the exact historical recording scope of an explicit selected corpus.
/// It reads current native game bodies and retained testimony twice without admitting or
/// repairing newer completion metadata. Ordinary ingestion semantics are unchanged.</summary>
public static class ChessRecordedCorpusVerification
{
    public sealed record Options(string ManifestPath, string ExpectedSha256,
        string EvidenceDirectory, int DeadlineSeconds = 3600);
    public sealed record Result(bool Completed, int SelectedGames, int ReadbackGames,
        long ReadbackPlies, string ReceiptPath);

    internal static void ValidateOptions(Options options)
    {
        if (!Path.IsPathFullyQualified(options.ManifestPath)
            || !Path.IsPathFullyQualified(options.EvidenceDirectory)
            || Path.GetFullPath(options.ManifestPath) == Path.GetFullPath(options.EvidenceDirectory)
            || !ChessRecordedSelection.Hex(options.ExpectedSha256, 64)
            || options.DeadlineSeconds is < 30 or > 86400)
            throw new ArgumentException("recorded verification requires distinct absolute manifest/evidence paths, exact SHA256 and a supported deadline");
    }

    public static async Task<Result> RunAsync(Options options, CancellationToken ct = default)
    {
        ValidateOptions(options);
        options = options with { ManifestPath = Path.GetFullPath(options.ManifestPath),
            EvidenceDirectory = Path.GetFullPath(options.EvidenceDirectory) };
        if (Directory.Exists(options.EvidenceDirectory) || File.Exists(options.EvidenceDirectory))
            throw new IOException("recorded verification evidence directory must be new");
        Directory.CreateDirectory(options.EvidenceDirectory);
        await using (var marker = new FileStream(Path.Combine(options.EvidenceDirectory, "owner"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        string receiptPath = Path.Combine(options.EvidenceDirectory, "recorded-verification.json");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DeadlineSeconds));
        var token = deadline.Token;
        ChessRecordedSelection? selection = null;
        ChessCorpusEvidence? retained = null;
        var phases = new List<(string Name, ChessRecordingMeasurement Measurement)>();
        var failure = await ChessCorpusBenchmark.CaptureFailureAsync(async () =>
        {
            selection = await ChessRecordedSelection.LoadAsync(
                options.ManifestPath, options.ExpectedSha256, token);
            var preparation = ChessCorpusPreparation.FromRecordedSelection(selection);
            retained = await ChessCorpusEvidence.ImportRecordedSelectionAsync(
                Path.Combine(options.EvidenceDirectory, "retained-selection"), selection, token);
            var diagnostics = new ChessRecordingMeasurement.WriterDiagnosticLogger();
            // No BootstrapSourcesAsync or registry mutation occurs on this creation path.
            await using var ingestor = await ChessPgnIngestor.CreateRecordedVerifierAsync(diagnostics, token);

            async Task<ChessCorpusEvidence> VerifyAsync(string name, ChessCorpusEvidence baseline)
            {
                string directory = Path.Combine(options.EvidenceDirectory, name);
                var evidence = new ChessCorpusEvidence(directory, baseline);
                var measurement = ChessRecordingMeasurement.FromCorpus(preparation, evidence,
                    requireNoWriterWork: true);
                phases.Add((name, measurement));
                diagnostics.Measurement = measurement;
                await measurement.StartProgressAsync(Path.Combine(directory, "progress.json"), Console.Error.WriteLine);
                try
                {
                    await selection.VerifyRetainedEvidenceAsync(token);
                    await ingestor.IngestCorpusGamesAsync(preparation.ReadSelected(token), measurement, token);
                    await measurement.VerifyPgnUnchangedAsync(preparation.Source.Path, token);
                    await evidence.CompleteAsync(token);
                    await selection.VerifyRetainedEvidenceAsync(token);
                    if (!measurement.IsVerifiedNoOpReplay
                        || !ChessCorpusEvidence.WriterIsZero(measurement.Writer)
                        || measurement.Durability is not null || measurement.NovelGames != 0
                        || measurement.AppliedGames != 0 || measurement.ReadbackGames != selection.SelectedGames
                        || measurement.ReadbackPlies != selection.Plies)
                        throw new InvalidDataException("recorded selection verification changed scope or performed writer work");
                    measurement.Complete("completed");
                    return evidence;
                }
                catch (OperationCanceledException)
                {
                    measurement.Complete("cancelled", "recorded selection verification deadline or cancellation");
                    throw;
                }
                catch (Exception error)
                {
                    measurement.Complete("failed", error.Message);
                    throw;
                }
                finally
                {
                    diagnostics.Measurement = null;
                    await measurement.StopProgressAsync();
                    await measurement.WriteAsync(Path.Combine(directory, "recording.json"));
                }
            }

            // Establish every selected current body and scope before the independent
            // second retained-scope read. Both passes use the original sealed chunk boundaries.
            var current = await VerifyAsync("readback", retained);
            await VerifyAsync("replay", current);
            await selection.VerifyUnchangedAsync(token);
        });
        bool completed = failure is null;
        var first = phases.Count > 0 ? phases[0].Measurement : null;
        var result = new Result(completed, selection?.SelectedGames ?? 0,
            first?.ReadbackGames ?? 0, first?.ReadbackPlies ?? 0, receiptPath);
        var receipt = new
        {
            schema = "laplace.chess-recorded-corpus-verification/v1",
            status = failure?.Status ?? "completed", error = failure?.Error, errorType = failure?.ErrorType,
            startedAt, finishedAt = DateTimeOffset.UtcNow, options,
            manifest = selection?.Manifest, source = selection?.Source,
            selectionManifest = selection?.SelectionManifest,
            selectedGames = result.SelectedGames, readbackGames = result.ReadbackGames,
            readbackPlies = result.ReadbackPlies,
            playingIds = selection?.PlayingIds.Select(id => Convert.ToHexStringLower(id.ToBytes())).ToArray(),
            retainedBaseline = retained?.Summary,
            retainedScopeReplayVerified = completed && phases.Count == 2
                && phases.All(p => p.Measurement.Status == "completed"
                    && p.Measurement.IsVerifiedNoOpReplay
                    && ChessCorpusEvidence.WriterIsZero(p.Measurement.Writer)),
            phases = phases.Select(p => new { name = p.Name, recording = p.Measurement }).ToArray(),
            scope = "Exactly the immutable selected original PGN occurrences and retained complete chunks; current native full-line/body/witness readback followed by a second identical retained recording-scope verification. Both phases prohibit writer work before apply; bootstrap is skipped. Exact original entity, line-carrier and playing witness IDs and counts are unchanged. Later completion metadata and calculated repair lanes are excluded; this is not a claim that whole current ingestion is a no-op or that metadata upgrades were applied. This is not a capacity benchmark or a claim that the parent run completed.",
        };
        await File.WriteAllTextAsync(receiptPath + ".pending", JsonSerializer.Serialize(receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(receiptPath + ".pending", receiptPath, overwrite: false);
        return result;
    }
}
