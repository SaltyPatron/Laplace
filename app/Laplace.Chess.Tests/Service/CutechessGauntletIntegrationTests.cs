using Laplace.Chess.Service;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Drives the real cutechess-cli with both real engines through a complete color-swapped
/// opening pair. Everything else in this suite parses canned lines; this is the gate that
/// proves the spawned binaries, scheduler, parser, and PGN artifact agree with one another.
///
/// No-ops with a logged reason when the lab binaries are not installed, so a dev box without
/// cutechess is not a red build. CI hosts get them from scripts/bootstrap-chess-lab.sh.
/// </summary>
[Trait("Tier", "integration")]
public sealed class CutechessGauntletIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OneOpeningPair_VerifiesIdentities_SwapsColors_AndMatchesPgn()
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
            Rounds = 2,
            Depth = 1,
            StockfishElo = 2000,
            PairOpenings = true,
            PgnOut = pgn,
            Event = "chess-lab/test",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var events = new List<ChessLabEvent>();
        await foreach (var evt in CutechessRunner.RunAsync(options, cts.Token))
            events.Add(evt);

        var command = Assert.Single(events.OfType<ChessLabCommandEvent>());
        output.WriteLine(command.CommandLine);
        Assert.Contains("-openings", command.Arguments);
        Assert.Contains("-repeat", command.Arguments);
        Assert.Contains("option.Substrate=substrate", command.Arguments);

        var done = Assert.Single(events.OfType<ChessLabDoneEvent>());
        var stderr = events.OfType<ChessLabTerminalEvent>()
            .Where(t => t.Stream == ChessLabStream.Stderr).Select(t => t.Text).ToList();
        Assert.Equal(ChessLabJobState.Completed, done.FinalState);
        Assert.Empty(stderr);

        var terminal = events.OfType<ChessLabTerminalEvent>().ToList();
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Command);
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Send);
        Assert.Contains(terminal, t => t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Recv);
        Assert.Contains(terminal, t =>
            t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Recv
            && t.Text.StartsWith("id name Laplace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(terminal, t =>
            t.Stream == ChessLabStream.Uci && t.Direction == ChessLabDirection.Recv
            && t.Text.StartsWith("id name Stockfish", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            terminal.Where(t => t.Stream == ChessLabStream.Uci),
            t => Assert.False(string.IsNullOrEmpty(t.Engine)));

        // The live board is rebuilt from that traffic, so plies prove the parse still matches.
        var boards = events.OfType<ChessLabBoardEvent>().ToList();
        Assert.NotEmpty(boards);
        Assert.Equal(1, boards[0].Ply);

        var games = events.OfType<ChessLabGameEvent>().OrderBy(g => g.Index).ToList();
        Assert.Equal(2, games.Count);
        Assert.Equal((games[0].White, games[0].Black), (games[1].Black, games[1].White));
        Assert.Contains(events, e => e is ChessLabMetricEvent { Name: "wins" });
        Assert.True(File.Exists(pgn), "cutechess wrote no PGN");

        var pgnGames = ReadPgnTags(pgn);
        Assert.Equal(games.Count, pgnGames.Count);
        Assert.False(string.IsNullOrWhiteSpace(pgnGames[0].Fen));
        Assert.Equal(pgnGames[0].Fen, pgnGames[1].Fen);
        for (int i = 0; i < games.Count; i++)
        {
            Assert.Equal(games[i].White, pgnGames[i].White);
            Assert.Equal(games[i].Black, pgnGames[i].Black);
            Assert.Equal(ResultToken(games[i].Result), pgnGames[i].Result);
        }

        Directory.Delete(Path.GetDirectoryName(pgn)!, recursive: true);
    }

    private static string ResultToken(string result)
        => result.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];

    private static List<(string White, string Black, string Result, string? Fen)> ReadPgnTags(string path)
    {
        var games = new List<(string White, string Black, string Result, string? Fen)>();
        string? white = null, black = null, result = null, fen = null;
        foreach (var line in File.ReadLines(path).Append(""))
        {
            if (TryTag(line, "White", out var value)) white = value;
            else if (TryTag(line, "Black", out value)) black = value;
            else if (TryTag(line, "Result", out value)) result = value;
            else if (TryTag(line, "FEN", out value)) fen = value;

            if (string.IsNullOrWhiteSpace(line) && white is not null && black is not null && result is not null)
            {
                games.Add((white, black, result, fen));
                white = black = result = fen = null;
            }
        }
        return games;
    }

    private static bool TryTag(string line, string tag, out string value)
    {
        string prefix = $"[{tag} \"";
        if (line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith("\"]", StringComparison.Ordinal))
        {
            value = line[prefix.Length..^2];
            return true;
        }
        value = "";
        return false;
    }
}
