using System.Collections.Immutable;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Read-only composition accounting over exact retained original game windows.</summary>
[Trait("Tier", "perf")]
public sealed class ChessAdmissionBoundProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task RetainedOriginalWindowsCompareFramingBoundsWithoutChangingTheGrant()
    {
        string directory = Environment.GetEnvironmentVariable("LAPLACE_CHESS_ADMISSION_BASELINE_DIRECTORY")
            ?? throw new InvalidOperationException("set the retained corpus measurement directory");
        string receiptPath = Path.Combine(directory, "fresh", "recording.json");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
        var provenance = receipt.RootElement.GetProperty("corpusSource");
        var source = provenance.GetProperty("source").Deserialize<ChessCorpusPreparation.FileIdentity>(
            ChessCorpusPreparation.Json)!;
        var manifest = provenance.GetProperty("selectionManifest").Deserialize<ChessCorpusPreparation.FileIdentity>(
            ChessCorpusPreparation.Json)!;
        await ChessCorpusPreparation.RequireUnchangedAsync(source, CancellationToken.None);
        await ChessCorpusPreparation.RequireUnchangedAsync(manifest, CancellationToken.None);
        var selection = File.ReadLines(manifest.Path).Select(line =>
            JsonSerializer.Deserialize<ChessCorpusPreparation.Selection>(line, ChessCorpusPreparation.Json)!).ToArray();
        var windows = File.ReadLines(Path.Combine(directory, "fresh", "chunks.jsonl")).Select(line =>
            JsonSerializer.Deserialize<ChessCorpusEvidence.Chunk>(line, ChessCorpusPreparation.Json)!).ToArray();
        Assert.NotEmpty(windows);
        Assert.Equal(receipt.RootElement.GetProperty("corpusEvidence").GetProperty("readbackGames").GetInt32(),
            windows.Sum(window => window.Games));
        int total = windows.Sum(window => window.Games);
        CodepointPerfcache.LoadDefault();
        var games = ChessCorpusPreparation.ReadSelected(source.Path, selection.Take(total).ToArray(), CancellationToken.None)
            .Select(text => ChessPgnDecomposer.TryParseGame(text, requireCompleteSource: true)!).ToArray();
        Assert.Equal(total, games.Length);
        for (int i = 0; i < total; i++) ChessCorpusPreparation.ValidateSelection(selection[i], games[i]);
        var retainedReceipt = await ChessCorpusPreparation.IdentifyAsync(receiptPath, CancellationToken.None);
        foreach (var window in windows)
        {
            await ChessCorpusPreparation.RequireUnchangedAsync(window.Body, CancellationToken.None);
            await ChessCorpusPreparation.RequireUnchangedAsync(window.Scope, CancellationToken.None);
            var chosen = games.Skip(window.FirstSelectedGame).Take(window.Games).ToArray();
            Assert.Equal(window.Games, chosen.Length);
            Assert.Equal(window.Plies, chosen.Sum(game => (long)game.MoveIds.Length));
            int offset = 0;
            using var chunk = ChessPgnChunk.ComposeNext(chosen, chosen.Select(game => game.PlayingId).ToHashSet(),
                ref offset, long.MaxValue);
            Assert.Equal(chosen.Length, offset);
            Assert.Null(chunk.Record.DeferredContent);
            Assert.Null(chunk.Analyze.DeferredContent);
            long modeledBeforeBuild = chunk.ModeledSourceAdmissionBytes;
            var builders = new[] { chunk.Record, chunk.Analyze, chunk.Repair, chunk.RepairAnalyze };
            long[] serialized = builders.Select(builder => builder.StagedBytesEstimate).ToArray();
            var changes = new List<SubstrateChange>(builders.Length);
            try
            {
                foreach (var builder in builders) changes.Add(builder.Build());
                var corrected = default(IngestAdmissionSizing);
                var prior = default(IngestAdmissionSizing);
                var referenceUnion = default(IngestAdmissionSizing);
                ulong exactNativeVertices = 0, priorNativeVertices = 0, correctedNativeVertices = 0;
                for (int i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    var stages = change.IntentStages.ToArray();
                    var selected = Shape(change.Physicalities);
                    var raw = Shape(change.PhysicalityObservations);
                    var conservative = IngestAdmissionSizing.MeasureParts(serialized[i], stages, selected, raw,
                        (ulong)change.Entities.Length, (ulong)change.Attestations.Length, growingStages: true);
                    var current = IngestAdmissionSizing.MeasureGrowingBuilder(serialized[i], stages, selected, raw,
                        (ulong)change.Entities.Length, (ulong)change.Attestations.Length);
                    referenceUnion = referenceUnion.Add(conservative);
                    var oldNative = default(PhysicalityDescriptorSizing.Shape);
                    foreach (var stage in stages)
                    {
                        ulong forms = checked((ulong)stage.PhysicalityCount);
                        ulong vertices = forms == 0 ? 0 : checked((ulong)stage.TupleBuffer(
                            IntentStageTable.Physicalities).Len) / 32;
                        oldNative = oldNative.Add(new(forms, vertices, vertices));
                        priorNativeVertices += vertices;
                        correctedNativeVertices += PhysicalityDescriptorSizing.FromStageBound(stage).StoredVertices;
                    }
                    exactNativeVertices += PhysicalityDescriptorSizing.FromStages(stages).StoredVertices;
                    var old = conservative with
                    {
                        Source = oldNative.Add(raw.Forms == 0 ? selected : raw.Add(selected)),
                        Admitted = oldNative.Add(selected),
                    };
                    corrected = corrected.Add(current);
                    prior = prior.Add(old);
                }
                Assert.Equal(modeledBeforeBuild, corrected.ModeledSourcePayloadBytes);
                Assert.True(corrected.ModeledSourcePayloadBytes < prior.ModeledSourcePayloadBytes);
                Assert.True(corrected.ModeledSourcePayloadBytes <= referenceUnion.ModeledSourcePayloadBytes);
                Assert.True(correctedNativeVertices >= exactNativeVertices);
                output.WriteLine("CHESS_RETAINED_WINDOW_BOUND " + JsonSerializer.Serialize(new
                {
                    schema = "laplace.chess-admission-bound-comparison/v2",
                    retainedReceipt, source, manifest,
                    window.Index, window.FirstSelectedGame, window.Games, window.Plies,
                    window.GameBodiesSha256,
                    oldModeledBytes = prior.ModeledSourcePayloadBytes,
                    correctedModeledBytes = corrected.ModeledSourcePayloadBytes,
                    framingOnlyModeledBytes = referenceUnion.ModeledSourcePayloadBytes,
                    retainedCaptureReservationBytes = corrected.CaptureReservationBytes,
                    serializedBytes = corrected.SerializedBytes,
                    priorNativeVertices, correctedNativeVertices, exactNativeVertices,
                    oldSourceShape = prior.Source, correctedSourceShape = corrected.Source,
                    framingOnlySourceShape = referenceUnion.Source,
                    oldAdmittedShape = prior.Admitted, correctedAdmittedShape = corrected.Admitted,
                    unchangedProducerGrantBytes = ChessPgnIngestor.ResolvedChunkStagedBytes,
                    syzygyLargest = ChessTablebaseRuntime.Largest,
                    scope = "Exact original source games and sealed grouping; composition/model only, no database writes or recorded-rate claim.",
                }, ChessCorpusPreparation.Json));
            }
            finally
            {
                foreach (var change in changes)
                    foreach (var stage in change.IntentStages) stage.Dispose();
            }
        }
        await ChessCorpusPreparation.RequireUnchangedAsync(source, CancellationToken.None);
        await ChessCorpusPreparation.RequireUnchangedAsync(manifest, CancellationToken.None);
        await ChessCorpusPreparation.RequireUnchangedAsync(retainedReceipt, CancellationToken.None);
    }

    private static PhysicalityDescriptorSizing.Shape Shape(ImmutableArray<PhysicalityRow> rows)
    {
        ulong vertices = 0, widest = 0;
        foreach (var row in rows)
        {
            int values = row.TrajectoryXyzm?.Length ?? 0;
            Assert.Equal(0, values % 4);
            ulong width = checked((ulong)values / 4);
            vertices = checked(vertices + width);
            widest = Math.Max(widest, width);
        }
        return new((ulong)rows.Length, vertices, widest);
    }
}
