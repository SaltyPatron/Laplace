using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class OpeningSeedReplayTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public void TerminalMiniatureDoesNotConsumeARequestedOpening(int plies)
    {
        WithCorpus(path =>
        {
            // The first row produced the terminal start in retained gauntlet
            // 7ca360011672441d983ec35e53b6b8e9. Later rows must fill the schedule.
            var fens = OpeningSeed.Fens(path, plies, max: 2);
            Assert.Equal(2, fens.Count);
            Assert.Equal("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2", fens[0]);
            Assert.Equal("rnbqkbnr/ppp1pppp/8/3p4/3P4/8/PPP1PPPP/RNBQKBNR w KQkq d6 0 2", fens[1]);
            var modality = new ChessModality();
            Assert.All(fens, fen => Assert.Null(modality.Terminal(modality.FromFen(fen))));
        });
    }

    [Fact]
    public void PrefixBeforeTheMateRemainsAPlayableOpening()
    {
        WithCorpus(path =>
        {
            string fen = Assert.Single(OpeningSeed.Fens(path, plies: 3, max: 1));
            var modality = new ChessModality();
            var state = modality.FromFen(fen);
            Assert.Null(modality.Terminal(state));
            var mate = Assert.Single(modality.LegalActions(state), move => move.ToUci() == "d8h4");
            var terminal = modality.Apply(state, mate);
            Assert.NotNull(modality.Terminal(terminal));
            Assert.Empty(MoveGen.Legal(terminal.Board));
        });
    }

    [Fact]
    public void GeneratedPairedSuiteKeepsAllRequestedGamesAndOnlyPlayableStarts()
    {
        WithCorpus(path =>
        {
            string? previous = Environment.GetEnvironmentVariable("LAPLACE_CHESS_OPENINGS");
            try
            {
                Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", path);
                var options = new CutechessOptions
                {
                    Rounds = 24,
                    OpeningPlies = 10,
                    PgnOut = Path.Combine(Path.GetDirectoryName(path)!, "games.pgn")
                };
                Assert.True(CutechessRunner.TryPrepareOpeningSuite(options,
                    out var suite, out int requested, out int available, out var error), error);
                Assert.Equal(12, requested);
                Assert.Equal(requested, available);
                var positions = File.ReadAllLines(suite);
                Assert.Equal(requested, positions.Length);
                var modality = new ChessModality();
                Assert.All(positions, fen => Assert.Null(modality.Terminal(modality.FromFen(fen))));
                var arguments = CutechessRunner.BuildArguments(options, "laplace-uci", "stockfish").ToList();
                Assert.Equal("12", arguments[arguments.IndexOf("-rounds") + 1]);
                Assert.Equal("2", arguments[arguments.IndexOf("-games") + 1]);
            }
            finally { Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", previous); }
        });
    }

    private static void WithCorpus(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "opening-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "a.tsv");
        try
        {
            File.WriteAllText(path, "eco\tname\tpgn\n"
                + "A00\tFool's Mate\t1. f3 e5 2. g4 Qh4#\n"
                + "C20\tKing's Pawn\t1. e4 e5\n"
                + "D00\tQueen's Pawn\t1. d4 d5\n");
            test(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
