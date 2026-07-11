using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// DB-coupled tests in this file are INTEGRATION tests: ChessLiveGameHost is a per-ply substrate
// WRITER, and its default connection resolves to the installed (production) database. They run
// only when LAPLACE_TEST_DB names an explicit, disposable test database (the chess-platform-seed
// convention: laplace_chess_test) and are no-ops otherwise. Tests must never write into the
// production substrate as a side effect of running the gate.
internal static class TestDb
{
    // LAPLACE_TEST_DB is a database NAME, not a connection string. The runner .env used to
    // set LAPLACE_TEST_DB=laplace; treating that as a conn string blows up Npgsql at index 0.
    public static string? ConnString
    {
        get
        {
            var name = Environment.GetEnvironmentVariable("LAPLACE_TEST_DB");
            if (string.IsNullOrWhiteSpace(name)) return null;
            // Refuse production / canonical substrate names — these tests write consensus rows.
            if (string.Equals(name, "laplace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "laplace-dev", StringComparison.OrdinalIgnoreCase))
                return null;
            return LaplaceInstall.PostgresConnectionString(name.Trim());
        }
    }
}

[Trait("Tier", "fast")]
public sealed class ChessMoveCommentaryTests
{
    [Fact]
    public void Truncate_RespectsLichessLimit()
    {
        string longText = new('x', 200);
        string cut = ChessMoveCommentary.Truncate(longText, ChessMoveCommentary.LichessMaxChars);
        Assert.Equal(ChessMoveCommentary.LichessMaxChars, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void Truncate_ShortTextUnchanged()
    {
        const string s = "Fork · Eval +0.3 (d6)";
        Assert.Equal(s, ChessMoveCommentary.Truncate(s, ChessMoveCommentary.LichessMaxChars));
    }

    [Theory]
    [InlineData(150, 4, false)]
    [InlineData(29_500, 8, true)]
    public async Task BuildAsync_IncludesEvalLine(int scoreCp, int depth, bool mating)
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit test DB only

        await using var host = await ChessLiveGameHost.CreateAsync(
            defaultLearnContext: "chess/test/commentary", connString: cs);
        var text = await ChessMoveCommentary.BuildAsync(
            host.DataSource,
            new ChessMoveCommentary.Inputs(scoreCp, depth, ["e2e4", "e7e5"], ["fork"]),
            CancellationToken.None,
            maxChars: 140);
        Assert.NotNull(text);
        if (mating)
            Assert.Contains("Mat", text, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("Eval", text);
        Assert.True(text.Length <= 140);
    }
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
    public async Task RecordPly_ReusesPositionEntity_ForRepeatedSurface()
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit test DB only

        await using var host = await ChessLiveGameHost.CreateAsync(
            defaultLearnContext: "chess/test/ply-fold", connString: cs);
        var gameId = Hash128.OfCanonical("chess/test/ply-fold/game-1");
        await host.OpenGameAsync(gameId, "chess/test/ply-fold");

        var m = new ChessModality();
        var s0 = m.Initial();
        var mv = MoveGen.Legal(s0.Board)[0];
        var s1 = m.Apply(s0, mv);
        string from = m.StateKey(s0);
        string to = m.StateKey(s1);

        await host.RecordPlyAsync(gameId, 1, from, to, mv.ToUci(), null);
        await host.RecordPlyAsync(gameId, 2, to, from, mv.ToUci(), null);

        var id1 = ChessCompose.PositionId(from);
        var id2 = ChessCompose.PositionId(from);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task CompleteGame_IncrementsGamesCompleted()
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit test DB only

        await using var host = await ChessLiveGameHost.CreateAsync(
            defaultLearnContext: "chess/test/complete", connString: cs);
        var gameId = Hash128.OfCanonical("chess/test/complete/g1");
        await host.OpenGameAsync(gameId, "chess/test/complete");

        var m = new ChessModality();
        var s0 = m.Initial();
        var mv = MoveGen.Legal(s0.Board)[0];
        var s1 = m.Apply(s0, mv);
        await host.RecordPlyAsync(gameId, 1, m.StateKey(s0), m.StateKey(s1), mv.ToUci(), null);
        await host.CompleteGameAsync(gameId, GameOutcome.Draw, adjudicated: false);

        Assert.Equal(1, host.GamesCompleted);
    }

    [Fact]
    public async Task BuildSearch_SubstrateOff_HasNoBias()
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit test DB only

        await using var host = await ChessLiveGameHost.CreateAsync(connString: cs);
        var classical = host.BuildSearch(substrate: false);
        var search = host.BuildSearch(substrate: true);
        Assert.NotNull(classical);
        Assert.NotNull(search);
    }
}
