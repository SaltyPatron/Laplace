using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessOpeningsDecomposerTests
{
    private static readonly string OpeningsDir = ChessCorpusPaths.Openings;

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
    public void CatalogLine_CollidesWithTryReplayLine_AndCarriesTrajectory()
    {
        var sans = ChessOpeningsDecomposer.ExtractSans("1. e4 e5 2. Nf3 Nc6 3. Bb5");
        var change = ChessOpeningsDecomposer.ComposeLineForTest("C60", "Ruy Lopez", sans);

        var replayed = ChessPgnDecomposer.TryReplayLine(sans, startFen: null);
        Assert.NotNull(replayed);
        var expectedLine = ChessCompose.LineId(replayed!);

        var lineEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        Assert.Equal(expectedLine, lineEntity.Id);

        var traj = Assert.Single(change.Physicalities, p => p.EntityId == expectedLine);
        Assert.NotNull(traj.TrajectoryXyzm);
        Assert.Equal(sans.Count + 1, traj.NConstituents);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.MoveType);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.SubstructureType);

        Assert.Contains(change.Attestations,
            a => a.TypeId == ChessVocabulary.OpeningNameType && a.SubjectId == expectedLine);
        Assert.Contains(change.Attestations,
            a => a.TypeId == ChessVocabulary.EcoCodeType && a.SubjectId == expectedLine);
        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == replayed![^1]
                 && (a.TypeId == ChessVocabulary.OpeningNameType
                     || a.TypeId == ChessVocabulary.EcoCodeType));
    }

    [Fact]
    public void Transposition_DifferentOpeningLines_SameFinalBoard()
    {
        var direct = ChessOpeningsDecomposer.ComposeLineForTest(
            "D30", "Queen's Gambit Declined", ["d4", "d5", "c4", "e6"]);
        var transposed = ChessOpeningsDecomposer.ComposeLineForTest(
            "A13", "English Opening: Agincourt Defense", ["c4", "e6", "d4", "d5"]);

        var lineA = Assert.Single(direct.Entities, e => e.TypeId == ChessVocabulary.GameType).Id;
        var lineB = Assert.Single(transposed.Entities, e => e.TypeId == ChessVocabulary.GameType).Id;
        Assert.NotEqual(lineA, lineB);

        var finalA = ChessPgnDecomposer.TryReplayLine(["d4", "d5", "c4", "e6"], null)![^1];
        var finalB = ChessPgnDecomposer.TryReplayLine(["c4", "e6", "d4", "d5"], null)![^1];
        Assert.Equal(finalA, finalB);
    }

    [SkippableFact]
    public void RealOpeningsBook_AllLinesResolve()
    {
        Skip.IfNot(Directory.Exists(OpeningsDir), "openings book directory not present");

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
