using System.Text;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessOpeningsDecomposerTests
{
    private static readonly string OpeningsDir =
        @"D:\Data\Ingest\Games\Chess\openings";

    [Fact]
    public void ParseRow_SkipsHeader()
        => Assert.Null(ChessOpeningsDecomposer.ParseRow("eco\tname\tpgn"));

    [Theory]
    [InlineData("")]
    [InlineData("A00\tName only")]
    [InlineData("A00\tName\t")]
    public void ParseRow_RejectsMalformed(string line)
        => Assert.Null(ChessOpeningsDecomposer.ParseRow(line));

    [Fact]
    public void ParseRow_SplitsColumns()
    {
        var row = ChessOpeningsDecomposer.ParseRow("C60\tRuy Lopez\t1. e4 e5 2. Nf3 Nc6 3. Bb5");
        Assert.NotNull(row);
        Assert.Equal("C60", row!.Value.Eco);
        Assert.Equal("Ruy Lopez", row.Value.Name);
        Assert.Equal("1. e4 e5 2. Nf3 Nc6 3. Bb5", row.Value.Movetext);
    }

    [Fact]
    public void ExtractSans_DropsMoveNumbers_KeepsMainline()
    {
        var sans = ChessOpeningsDecomposer.ExtractSans("1. e4 e5 2. Nf3 Nc6 3. Bb5");
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" }, sans);
    }

    [Fact]
    public void ExtractSans_HandlesCastlingAndCaptures()
    {
        var sans = ChessOpeningsDecomposer.ExtractSans("1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 4. b4 Bxb4 5. c3 Ba5 6. O-O");
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6", "Bc4", "Bc5", "b4", "Bxb4", "c3", "Ba5", "O-O" }, sans);
    }

    [Fact]
    public void Replays_RuyLopez_ToExpectedPosition()
    {
        var sans = ChessOpeningsDecomposer.ExtractSans("1. e4 e5 2. Nf3 Nc6 3. Bb5");
        Assert.Equal(
            "r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3",
            Replay(sans));
    }

    [Fact]
    public void RealOpeningsBook_AllLinesResolve()
    {
        if (!Directory.Exists(OpeningsDir)) return;

        int total = 0, resolved = 0;
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(OpeningsDir, "*.tsv"))
        foreach (var line in File.ReadLines(file, Encoding.UTF8))
        {
            if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
            total++;
            var sans = ChessOpeningsDecomposer.ExtractSans(row.Movetext);
            if (sans.Count > 0 && TryReplay(sans))
                resolved++;
            else if (failures.Count < 10)
                failures.Add($"{row.Eco} {row.Name}: {row.Movetext}");
        }

        Assert.True(total > 2000, $"expected the full ECO book (~3700 lines), got {total}");
        Assert.True(resolved == total,
            $"{total - resolved}/{total} opening lines failed to parse+replay. First few:\n  "
            + string.Join("\n  ", failures));
    }

    private static string Replay(IReadOnlyList<string> sans)
    {
        var m = new ChessModality();
        var s = m.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            Assert.True(mv is not null, $"unresolved SAN '{san}'");
            s = m.Apply(s, mv!.Value);
        }
        return s.Board.ToFen();
    }

    private static bool TryReplay(IReadOnlyList<string> sans)
    {
        var m = new ChessModality();
        var s = m.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            if (mv is null) return false;
            s = m.Apply(s, mv.Value);
        }
        return true;
    }
}
