using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;
using Inventory = Laplace.Chess.Service.ChessStartingSideInventory;
using Export = Laplace.Chess.Service.ChessRecordedFloorExport;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordedFloorExportTests
{
    [Fact]
    public async Task ReobservedLineKeepsPlayingOccurrencesAndOnePersistentTransitionPerKey()
    {
        using var directory = new ExportDirectory();
        var first = Game(1, ["e4", "e5", "Nf3", "Nc6"]);
        var second = first with { PlayingId = Id(2) };
        var receipt = await Export.ExportAsync(directory.Options(), new Source([first, second]), CancellationToken.None);
        Assert.Equal("completed", receipt.Status);
        Assert.True(receipt.InventoryComplete);
        Assert.Equal(2, receipt.ExportedPlayings);
        Assert.Equal(10, receipt.PositionOccurrences);
        Assert.Equal(8, receipt.TransitionOccurrences);
        Assert.Equal((ulong)4, receipt.UniqueTransitions);
        Assert.Equal(4, receipt.Files.Count);
        Assert.Equal(10, File.ReadAllLines(Path.Combine(directory.Path, "positions.txt")).Length);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "export-receipt.json")));
        Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("snapshot").GetBoolean());
        // AddBlob verifies the produced v1 checksum/order through the existing owner,
        // without installing it into this test process's serving floor.
        using var merge = new ChessTransitionFloorBuilder(directory.Path + "-verify", 2, 2, 1024 * 1024);
        merge.AddBlob(Path.Combine(directory.Path, "transitions.bin"));
        var readback = merge.Complete(Path.Combine(directory.Path, "readback.bin"));
        Assert.Equal((ulong)4, readback.Records);
        Assert.Equal(File.ReadAllBytes(Path.Combine(directory.Path, "transitions.bin")),
            File.ReadAllBytes(Path.Combine(directory.Path, "readback.bin")));
    }

    [Fact]
    public async Task MissingAdmittedReplayCannotPublishACompletedExport()
    {
        using var directory = new ExportDirectory();
        var game = Game(1, ["e4"]) with { AdmittedReplay = null };
        var receipt = await Export.ExportAsync(directory.Options(), new Source([game]), CancellationToken.None);
        Assert.Equal("partial", receipt.Status);
        Assert.False(receipt.InventoryComplete);
        Assert.Equal(0, receipt.ExportedPlayings);
        Assert.False(File.Exists(Path.Combine(directory.Path, "transitions.bin")));
        Assert.Empty(receipt.Files);
    }

    [Fact]
    public async Task BoundedOutputRefusalPreservesPartialReceiptAndDoesNotPublishTransitionFloor()
    {
        using var directory = new ExportDirectory();
        var receipt = await Export.ExportAsync(directory.Options() with { MaximumExportBytes = 81 },
            new Source([Game(1, ["e4", "e5"])]), CancellationToken.None);
        Assert.Equal("partial", receipt.Status);
        Assert.False(receipt.InventoryComplete);
        Assert.False(File.Exists(Path.Combine(directory.Path, "transitions.bin")));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "inventory", "summary.json")));
        Assert.Equal("export-verified-game", summary.RootElement.GetProperty("Stage").GetString());
        Assert.Equal(1, summary.RootElement.GetProperty("Unclassified").GetInt32());
    }

    [Fact]
    public async Task CompleteAdmittedTypedLineBeyondDisplayWindowExportsEveryOccurrenceAndHonorsOutputGrant()
    {
        // A legal typed line is not a claim of a remotely played normal game. Repetition
        // permits a deterministic fixture to cross the ordinary display-only window.
        var sans = Enumerable.Range(0, 260).SelectMany(_ => new[] { "Nf3", "Nf6", "Ng1", "Ng8" }).ToArray();
        var game = Game(1, sans);
        Assert.Equal(1040, game.MoveIds.Count);
        Assert.NotNull(game.AdmittedReplay);
        Assert.Null(game.AdmittedReplay!.Truncated);
        Assert.Equal(1040, game.AdmittedReplay.Plies.Count);
        var displayed = ChessReplay.Replay(game.MoveIds, game.StartFen);
        Assert.Equal(1024, displayed.Plies.Count);
        Assert.NotNull(displayed.Truncated);

        using var directory = new ExportDirectory();
        var receipt = await Export.ExportAsync(directory.Options(), new Source([game]), CancellationToken.None);
        Assert.Equal("completed", receipt.Status);
        Assert.True(receipt.InventoryComplete);
        Assert.Equal(1, receipt.ExportedPlayings);
        Assert.Equal(1041, receipt.PositionOccurrences);
        Assert.Equal(1040, receipt.TransitionOccurrences);
        Assert.Equal((ulong)4, receipt.UniqueTransitions);
        Assert.Equal(1041, File.ReadAllLines(Path.Combine(directory.Path, "positions.txt")).Length);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "inventory", "summary.json")));
        Assert.Equal("completed", summary.RootElement.GetProperty("Status").GetString());
        Assert.Equal(1, summary.RootElement.GetProperty("Retained").GetInt64());

        // The same actual exporter must refuse a grant one byte below its
        // measured occurrence work. Refusal may retain a prefix, never a complete floor.
        using var refusedDirectory = new ExportDirectory();
        var refused = await Export.ExportAsync(refusedDirectory.Options() with
        {
            MaximumExportBytes = receipt.ExportWorkBytesReserved - 1
        }, new Source([game]), CancellationToken.None);
        Assert.Equal("partial", refused.Status);
        Assert.False(refused.InventoryComplete);
        Assert.Equal(0, refused.ExportedPlayings);
        Assert.False(File.Exists(Path.Combine(refusedDirectory.Path, "transitions.bin")));
        using var refusedSummary = JsonDocument.Parse(File.ReadAllText(Path.Combine(refusedDirectory.Path, "inventory", "summary.json")));
        Assert.Equal("export-verified-game", refusedSummary.RootElement.GetProperty("Stage").GetString());
        Assert.Equal(1, refusedSummary.RootElement.GetProperty("Unclassified").GetInt32());
    }

    [Fact]
    public async Task FailedLaterPageKeepsActualObservedCountsAndCannotCompleteAnEarlierPrefix()
    {
        using var directory = new ExportDirectory();
        var source = new Source([Game(1, ["e4"]), Game(2, ["d4"])]) { FailHydrationPage = 2 };
        var receipt = await Export.ExportAsync(directory.Options() with
        {
            Inventory = directory.Options().Inventory with { PageSize = 1 }
        }, source, CancellationToken.None);
        Assert.Equal("partial", receipt.Status);
        Assert.Equal(2, receipt.SelectedPlayings);
        Assert.Equal(1, receipt.ExportedPlayings);
        Assert.Equal(2, receipt.PositionOccurrences);
        Assert.False(receipt.InventoryComplete);
        Assert.False(File.Exists(Path.Combine(directory.Path, "transitions.bin")));
    }

    [Fact]
    public void ExportOptionsRejectUnknownDuplicateAndOverflowedBounds()
    {
        Assert.Throws<ArgumentException>(() => Export.Parse(["--output-dir", "/build/test",
            "--maximum-export-mib", "1", "--maximum-export-mib", "2"]));
        Assert.Throws<ArgumentException>(() => Export.Parse(["--output-dir", "/build/test", "--merge-fan-in", "1"]));
        Assert.Throws<ArgumentException>(() => Export.Parse(["--output-dir", "/build/test", "--unknown", "7"]));
        Assert.Throws<OverflowException>(() => Export.Parse(["--output-dir", "/build/test",
            "--maximum-spill-mib", long.MaxValue.ToString()]));
    }

    private static Hash128 Id(byte value)
    {
        var bytes = new byte[16];
        bytes[0] = value;
        return Hash128.FromBytes(bytes);
    }

    private static ChessWitnessedGame Game(byte id, IReadOnlyList<string> sans)
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var start = ChessCompose.PositionId(board);
        var ids = new List<Hash128>(sans.Count);
        var scratch = new List<ChessMove>();
        foreach (string san in sans)
        {
            var move = San.Resolve(board, san, scratch);
            Assert.NotNull(move);
            Assert.True(MoveGen.IsLegal(board, move!.Value));
            ids.Add(ChessCompose.MoveId(board.Squares[move.Value.From], move.Value));
            MoveApply.Make(board, move.Value);
        }
        var replay = ChessWitnessHydrator.ReplayAdmittedLine(ids, ChessModality.StartFen);
        return new ChessWitnessedGame(ChessCompose.LineId(start, ids.ToArray()), Id(id), sans,
            new GameOutcome(null), null, null, ChessModality.StartFen, null, null, null)
        {
            MoveIds = ids, StartPositionId = start, AdmittedReplay = replay
        };
    }

    // Actual DB source ownership/hydration is covered by its owner. This fixture
    // exercises complete-only artifact orchestration and the real binary producers.
    private sealed class Source(ChessWitnessedGame[] games) : Inventory.IReadSource
    {
        public int? FailHydrationPage { get; init; }
        private int pages;
        public Task<Inventory.DatabaseIdentity> IdentityAsync(CancellationToken ct)
            => Task.FromResult(new Inventory.DatabaseIdentity("fixture", "123", "456"));
        public Task<long?> CountAsync(CancellationToken ct) => Task.FromResult<long?>(games.Length);
        public Task<IReadOnlyList<Hash128>> PageAsync(byte[] afterId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Hash128>>(games
                .Where(game => afterId.Length == 0 || Hash128.FromBytes(afterId).CompareToBytewise(game.PlayingId) < 0)
                .Take(limit).Select(game => game.PlayingId).ToArray());
        public Task<Inventory.PageInputs> HydrateAsync(IReadOnlyList<Hash128> selected, long maximumBytes, CancellationToken ct)
        {
            if (++pages == FailHydrationPage) throw new InvalidDataException("controlled missing witnessed input");
            var chosen = games.Where(game => selected.Contains(game.PlayingId)).ToArray();
            return Task.FromResult(new Inventory.PageInputs(chosen, chosen.ToDictionary(game => game.PlayingId,
                game => (IReadOnlyList<Inventory.SourceBinding>)[
                    new("ChessPgn", ChessVocabulary.PgnSourceId.ToString(), game.LineId.ToString())])));
        }
    }

    private sealed class ExportDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
            "recorded-floor-export-" + Guid.NewGuid().ToString("N"));
        public Export.Options Options() => new(new Inventory.Options(Path, 2, 32 * 1024 * 1024,
            32 * 1024 * 1024, 60), 2, 2, 32 * 1024 * 1024, 32 * 1024 * 1024);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
