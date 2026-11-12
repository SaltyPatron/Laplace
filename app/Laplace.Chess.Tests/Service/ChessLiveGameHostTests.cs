using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// DB-coupled tests in this project are INTEGRATION tests: they run only when LAPLACE_TEST_DB names an
// explicit, disposable test database. Missing configuration fails the database tier. Tests never read or
// write the installed substrate as a side effect of running the gate.
internal static class TestDb
{
    // LAPLACE_TEST_DB is a database NAME, not a connection string. The runner .env used to
    // set LAPLACE_TEST_DB=laplace; treating that as a conn string blows up Npgsql at index 0.
    public static string RequireConnectionString()
    {
        var name = Environment.GetEnvironmentVariable("LAPLACE_TEST_DB")?.Trim();
        if (string.IsNullOrWhiteSpace(name)
            || new[] { "laplace", "laplace-mono", "laplace-app", "laplace-desktop-app" }
                .Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("LAPLACE_TEST_DB must name an explicit disposable test database.");
        return LaplaceInstall.PostgresConnectionString(name);
    }
}

/// <summary>
/// The Engine as a game recorder sees it: each read is kept (the runtime witness stating it, the file, the
/// PGN the file held) and answers with <see cref="ExitCode"/>. No process starts and no database is opened.
/// </summary>
internal sealed class RecordingEngine : IDisposable
{
    public readonly List<(string? Witness, string Path, string Pgn)> Reads = new();
    public int ExitCode;
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("laplace-chess-games-").FullName;

    public ChessGameRecorder Recorder()
        => new("Host=localhost;Database=laplace-chess-test-none", Directory, Execute);

    /// <summary>A host that records through this engine. Its datasource is absent: a search may not read.</summary>
    public ChessLiveGameHost Host() => new(null!, Recorder());

    private Task<int> Execute(NativeRecipeRequest request, Stream output, Stream error, CancellationToken ct)
    {
        Assert.Null(request.Source);
        lock (Reads) Reads.Add((request.Witness?.Name, request.Paths[0], File.ReadAllText(request.Paths[0])));
        if (ExitCode != 0) error.Write("fixture: the Engine refused the file"u8);
        return Task.FromResult(ExitCode);
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

public sealed class ChessLiveGameHostTests
{
    [Fact]
    [Trait("Tier", "fast")]
    public void LichessGameId_IsStable()
    {
        var a = ChessLiveGameHost.LichessGameId("abc123");
        var b = ChessLiveGameHost.LichessGameId("abc123");
        Assert.Equal(a, b);
        Assert.NotEqual(ChessLiveGameHost.LichessGameId("other"), a);
    }

    [Fact]
    [Trait("Tier", "fast")]
    public void LichessMetadataReaders_PreserveProviderValues()
    {
        using var doc = JsonDocument.Parse("""
            {
              "createdAt": 0,
              "speed": "blitz",
              "clock": { "initial": 300000, "increment": 5000 },
              "white": { "rating": 1800 },
              "black": { "rating": 1900 }
            }
            """);
        var game = doc.RootElement;

        Assert.Equal("1970.01.01", LichessBot.ReadCreatedDate(game));
        Assert.Equal("300+5", LichessBot.ReadTimeControl(game));
        Assert.Equal("blitz", LichessBot.ReadSpeedClass(game));
        Assert.Equal(1800, LichessBot.ReadPlayerRating(game, "white"));
        Assert.Equal(1900, LichessBot.ReadPlayerRating(game, "black"));
    }

    [Theory]
    [InlineData("w", "e2e4", 0)]
    [InlineData("b", "e7e5", 1)]
    public void RecordedMoverComesFromResolvedBoardNotPlyParity(string side, string uci, int expectedMover)
    {
        var modality = new ChessModality();
        var fen = ChessModality.StartFen.Replace(" w ", $" {side} ", StringComparison.Ordinal);
        var before = modality.FromFen(fen);
        var move = Assert.Single(MoveGen.Legal(before.Board), candidate => candidate.ToUci() == uci);
        var after = modality.Apply(before, move);
        var recorded = ChessLiveGameHost.ResolveRecordedPly(
            modality.StateKey(before), modality.StateKey(after), uci);

        Assert.Equal(expectedMover, recorded.MoverSide);
        Assert.Equal(uci, recorded.Uci);
        Assert.Equal(move, recorded.Move);
        Assert.Equal(before.Board.ToFen(), recorded.FromFen);
        Assert.Equal(side == "w" ? "e4" : "e5", recorded.San);
    }

    [Fact]
    public async Task LiveScopeReleasesPopulatedSessionWithoutRecordingAGame()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();
        var id = ChessLiveGameHost.LichessGameId("scope-abandon-fixture");
        await host.OpenGameAsync(id);
        var scope = host.CaptureGameScope(id);
        Assert.NotNull(scope);
        var modality = new ChessModality();
        var before = modality.Initial();
        var move = Assert.Single(MoveGen.Legal(before.Board), candidate => candidate.ToUci() == "e2e4");
        var after = modality.Apply(before, move);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using (scope)
            {
                await host.RecordPlyAsync(id, 1, modality.StateKey(before), modality.StateKey(after), "e2e4");
                await host.RecordPlyClockAsync(id, 1, 1000);
                await host.RecordPlyAnalysisAsync(id, 1, new ChessLivePlyAnalysis(5, 2, 100));
                throw new IOException("fixture interrupted stream");
            }
        });
        scope.Dispose();
        Assert.Null(host.CaptureGameScope(id));
        Assert.Equal(0, host.GamesCompleted);
        Assert.Empty(engine.Reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RecordPlyAsync(id, 2, modality.StateKey(before), modality.StateKey(after), "e2e4"));
    }

    [Fact]
    public async Task OldScopeCannotAbandonReplacementSessionForSameLichessId()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();
        var id = ChessLiveGameHost.LichessGameId("scope-reconnect-fixture");
        await host.OpenGameAsync(id);
        var oldScope = host.CaptureGameScope(id);
        Assert.NotNull(oldScope);
        await host.OpenGameAsync(id);
        var newScope = host.CaptureGameScope(id);
        Assert.NotNull(newScope);

        oldScope.Dispose();
        Assert.NotNull(host.CaptureGameScope(id));
        newScope.Dispose();
        Assert.Null(host.CaptureGameScope(id));
        Assert.Equal(0, host.GamesCompleted);
    }

    private static async Task<Hash128> PlayAsync(
        ChessLiveGameHost host, Hash128 id, GameOutcome outcome, int[]? clocksMs, params string[] ucis)
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        for (int i = 0; i < ucis.Length; i++)
        {
            var move = Assert.Single(MoveGen.Legal(state.Board), candidate => candidate.ToUci() == ucis[i]);
            var next = modality.Apply(state, move);
            await host.RecordPlyAsync(id, i + 1, modality.StateKey(state), modality.StateKey(next), ucis[i]);
            if (clocksMs is not null) await host.RecordPlyClockAsync(id, i + 1, clocksMs[i]);
            state = next;
        }
        return await host.CompleteGameAsync(id, outcome, adjudicated: false);
    }

    [Fact]
    public async Task CompleteGame_WritesTheGameAsThePgnTheWitnessLaplaceStates()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();
        var id = ChessLiveGameHost.SessionHandle(Guid.NewGuid());
        await host.OpenGameAsync(id, "Alice", "Laplace", new ChessLiveGameMetadata(
            Event: "Fixture", Site: "Laplace", Date: "2026.11.12", TimeControl: "180+2",
            ExternalGameId: "lichess:abc", WhiteRating: 1800));

        var line = await PlayAsync(host, id, GameOutcome.WonBy(0), [180_000, 179_500, 178_000], "e2e4", "e7e5", "g1f3");

        var read = Assert.Single(engine.Reads);
        Assert.Equal(ChessGameRecorder.Witness, read.Witness);
        Assert.Equal(
            "[Event \"Fixture\"]\n[Site \"Laplace\"]\n[Date \"2026.11.12\"]\n[Round \"-\"]\n[White \"Alice\"]\n"
            + "[Black \"Laplace\"]\n[Result \"1-0\"]\n[WhiteElo \"1800\"]\n[TimeControl \"180+2\"]\n"
            + "[Termination \"normal\"]\n[GameId \"lichess:abc\"]\n\n"
            + "1. e4 {[%clk 0:03:00]} e5 {[%clk 0:02:59.5]} 2. Nf3 {[%clk 0:02:58]} 1-0\n\n",
            read.Pgn);
        // One game is one file, named by its text.
        Assert.Equal(
            Convert.ToHexStringLower(Hash128.Blake3(Encoding.UTF8.GetBytes(read.Pgn)).ToBytes()) + ".pgn",
            Path.GetFileName(read.Path));
        Assert.Equal(engine.Directory, Path.GetDirectoryName(read.Path));
        Assert.Equal(1, host.GamesCompleted);

        // The id returned is the Engine's id of the line the game played.
        var board = Board.FromFen(ChessModality.StartFen);
        var moves = new List<ChessMove>();
        foreach (var uci in new[] { "e2e4", "e7e5", "g1f3" })
        {
            var move = MoveGen.Legal(board).Single(candidate => candidate.ToUci() == uci);
            moves.Add(move);
            MoveApply.Make(board, move);
        }
        Assert.Equal(
            ChessEngineIdentity.LineId(ChessEngineIdentity.Trajectory(Board.FromFen(ChessModality.StartFen), moves)),
            line);
    }

    [Fact]
    public async Task TheSameGamePlayedTwiceIsTheSameFile()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();
        var metadata = new ChessLiveGameMetadata(Event: "Fixture", Site: "Laplace", Date: "2026.11.12");
        foreach (var _ in new[] { 0, 1 })
        {
            var id = ChessLiveGameHost.SessionHandle(Guid.NewGuid());
            await host.OpenGameAsync(id, "A", "B", metadata);
            await PlayAsync(host, id, GameOutcome.Draw, null, "e2e4");
        }
        Assert.Equal(2, engine.Reads.Count);
        Assert.Equal(engine.Reads[0].Path, engine.Reads[1].Path);
        Assert.Single(Directory.GetFiles(engine.Directory));
    }

    [Fact]
    public async Task AGameTheEngineRefusesIsNotReportedRecorded()
    {
        using var engine = new RecordingEngine { ExitCode = 3 };
        var host = engine.Host();
        var id = ChessLiveGameHost.SessionHandle(Guid.NewGuid());
        await host.OpenGameAsync(id, "A", "B");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PlayAsync(host, id, GameOutcome.Draw, null, "e2e4"));
        Assert.Contains("the Engine refused the file", failure.Message);
        Assert.Equal(0, host.GamesCompleted);
        // The game is still open: a retry records it.
        engine.ExitCode = 0;
        Assert.NotEqual(Hash128.Zero, await host.CompleteGameAsync(id, GameOutcome.Draw, adjudicated: false));
        Assert.Equal(1, host.GamesCompleted);
    }

    [Fact]
    public async Task AGameOfNoMovesRecordsNothing()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();
        var id = ChessLiveGameHost.SessionHandle(Guid.NewGuid());
        await host.OpenGameAsync(id, "A", "B");
        Assert.Equal(Hash128.Zero, await host.CompleteGameAsync(id, GameOutcome.Draw, adjudicated: true));
        Assert.Empty(engine.Reads);
    }

    [Fact]
    public void MatchRunner_LiveHost_RecordsEachGameOnce()
    {
        using var engine = new RecordingEngine();
        var host = engine.Host();

        var result = MatchRunner.Play(
            () => MatchRunner.RandomChooser,
            () => MatchRunner.RandomChooser,
            games: 2,
            maxPlies: 2,
            seed: 17,
            concurrency: 1,
            openingPlies: 0,
            liveHost: host,
            liveLearnContext: "chess/test/match-runner",
            aPlayerName: "A", bPlayerName: "B");

        Assert.Equal(2, result.Games);
        Assert.Equal(2, host.GamesCompleted);
        Assert.All(engine.Reads, read => Assert.Equal(ChessGameRecorder.Witness, read.Witness));
        Assert.All(engine.Reads, read => Assert.Contains("[White \"", read.Pgn));
    }

    [Fact]
    [Trait("Category", "RequiresDatabase")]
    public async Task BuildSearch_WithAndWithoutRecordedEvidence()
    {
        var cs = TestDb.RequireConnectionString();

        await using var host = await ChessLiveGameHost.CreateAsync(connString: cs);
        var classical = host.BuildSearch(substrate: false);
        var search = host.BuildSearch(substrate: true);
        Assert.NotNull(classical);
        Assert.NotNull(search);
    }
}
