using Laplace.Chess.Service;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Drives the real cutechess-cli, with real engines, for one round. Everything else in this
/// suite parses canned lines; only this notices when the binary's argument grammar or output
/// format moves under us — which is exactly how the bare "-debug" regression shipped.
///
/// No-ops with a logged reason when the lab binaries are not installed, so a dev box without
/// cutechess is not a red build. CI hosts get them from scripts/bootstrap-chess-lab.sh.
/// </summary>
[Trait("Tier", "integration")]
public sealed class CutechessGauntletIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OneRound_Runs_StreamsATranscript_AndCompletes()
    {
        var catalog = ChessLabPaths.Catalog;
        if (!catalog["cutechess"].Found || !catalog["stockfish"].Found || !catalog["laplaceUci"].Found)
        {
            output.WriteLine(
                $"skipped — cutechess={catalog["cutechess"].Found} stockfish={catalog["stockfish"].Found} "
                + $"laplaceUci={catalog["laplaceUci"].Found}");
            return;
        }

        var pgn = Path.Combine(Path.GetTempPath(), $"laplace-gauntlet-{Guid.NewGuid():N}", "games.pgn");
        Directory.CreateDirectory(Path.GetDirectoryName(pgn)!);
        var options = new CutechessOptions
        {
            Rounds = 1,
            Depth = 1,
            StockfishElo = 2000,
            PgnOut = pgn,
            Event = "chess-lab/test",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var events = new List<ChessLabEvent>();
        await foreach (var evt in CutechessRunner.RunAsync(options, cts.Token))
            events.Add(evt);

        var command = Assert.Single(events.OfType<ChessLabCommandEvent>());
        output.WriteLine(command.CommandLine);

        var done = Assert.Single(events.OfType<ChessLabDoneEvent>());
        var stderr = events.OfType<ChessLabTerminalEvent>()
            .Where(t => t.Stream == ChessLabStream.Stderr).Select(t => t.Text).ToList();
        Assert.Equal(ChessLabJobState.Completed, done.FinalState);
        // A bare -debug puts "Warning: Empty value for option" here and nothing else anywhere.
        Assert.Empty(stderr);

        var terminal = events.OfType<ChessLabTerminalEvent>().ToList();
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Command);
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Send);
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Recv);
        Assert.All(
            terminal.Where(t => t.Stream == ChessLabStream.Uci),
            t => Assert.False(string.IsNullOrEmpty(t.Engine)));

        // The live board is rebuilt from that traffic, so plies prove the parse still matches.
        var boards = events.OfType<ChessLabBoardEvent>().ToList();
        Assert.NotEmpty(boards);
        Assert.Equal(1, boards[0].Ply);

        Assert.Contains(events, e => e is ChessLabGameEvent);
        Assert.Contains(events, e => e is ChessLabMetricEvent { Name: "wins" });
        Assert.True(File.Exists(pgn), "cutechess wrote no PGN");

        Directory.Delete(Path.GetDirectoryName(pgn)!, recursive: true);
    }
}
