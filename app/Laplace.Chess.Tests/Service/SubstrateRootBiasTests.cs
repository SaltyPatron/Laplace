using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Root evidence is each legal move's [position, move, position] claim and its Native standing: the
/// conservative rating relative to the neutral prior, shrunk by the games folded into it, as centipawns.
/// </summary>
public sealed class SubstrateRootBiasTests
{
    [Fact]
    public void ARecordedTransitionsStandingSteersTheRoot()
    {
        var fixture = Start("e2e4");
        var bias = Bias(ids => Present(ids, fixture.Claim, +10));

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(80, bonus[fixture.Index]);
        Assert.Equal(1, bonus.Count(static cp => cp != 0));
        Assert.Equal(1, bias.RootsWithExactEvidence);
        Assert.Equal(1, bias.ExactTransitionSignals);
    }

    [Fact]
    public void TheClaimReadIsTheEnginesTransitionOfEachLegalMove()
    {
        var fixture = Start("g1f3");
        IReadOnlyCollection<Hash128>? asked = null;
        var bias = Bias(ids => { asked = ids; return Empty(); });

        _ = bias.Bonus(fixture.Board, fixture.Moves);

        var expected = fixture.Moves.Select(move =>
        {
            var next = fixture.Board.Clone();
            MoveApply.Make(next, move);
            return ChessEngineIdentity.TransitionId(
                ChessEngineIdentity.PositionId(fixture.Board),
                ChessEngineIdentity.MoveId(fixture.Board, move),
                ChessEngineIdentity.PositionId(next));
        }).ToArray();
        Assert.Equal(expected, asked);
        Assert.Equal(fixture.Claim, expected[fixture.Index]);
    }

    [Fact]
    public void AMoveNoRecordedGamePlayedHasNoBonus()
    {
        var fixture = Start("e2e4");
        var bias = Bias(_ => Empty());

        Assert.All(bias.Bonus(fixture.Board, fixture.Moves), static cp => Assert.Equal(0, cp));
        Assert.Equal(0, bias.RootsWithExactEvidence);
        Assert.Equal(0, bias.ExactTransitionSignals);
    }

    [Fact]
    public void TenGamesAtUnitPrecisionKeepTwoThirdsOfTheEdgeAndATighterDeviationKeepsMore()
    {
        var fixture = Start("e2e4");
        var ordinary = Shrunk(fixture, points: 9, matches: 10, deviation: 0);
        var tighter = Shrunk(fixture, points: 9, matches: 10, deviation: GlickoPriors.InitialRd / 2e9);

        Assert.Equal(48, ordinary.Bonus(fixture.Board, fixture.Moves)[fixture.Index]);
        Assert.Equal(64, tighter.Bonus(fixture.Board, fixture.Moves)[fixture.Index]);
        Assert.Equal(5d, ChessShrink.ExactTransitionK0);
    }

    [Theory]
    [InlineData(+100, 150)]
    [InlineData(-100, -150)]
    public void TheBonusIsCapped(double points, int expected)
    {
        var fixture = Start("e2e4");
        var bias = Bias(ids => Present(ids, fixture.Claim, points));
        Assert.Equal(expected, bias.Bonus(fixture.Board, fixture.Moves)[fixture.Index]);
    }

    [Fact]
    public void RulesWithNoEngineIdentityReadNothing()
    {
        var board = Board.FromFen(ChessModality.StartFen, ChessVariants.Crazyhouse);
        int reads = 0;
        var bias = Bias(_ => { reads++; return Empty(); });

        Assert.All(bias.Bonus(board, MoveGen.Legal(board)), static cp => Assert.Equal(0, cp));
        Assert.Equal(0, reads);
        Assert.Equal(0, bias.RootReads);
    }

    [Fact]
    public void ReusesAFrontiersStandingsUntilTheyExpire()
    {
        var fixture = Start("e2e4");
        int reads = 0;
        long clock = 0;
        var bias = new SubstrateRootBias(ids =>
        {
            reads++;
            return Present(ids, fixture.Claim, +10);
        }, cpPerPoint: 8d, capCp: 150, shrinkK0: 0d, clock: () => clock);

        var before = bias.ObserveWork();
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        var cold = bias.ObserveWork();
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        var hit = bias.ObserveWork();
        Assert.Equal(0, before.BackendReads);
        Assert.Equal(1, cold.BackendReads);
        Assert.True(cold.FrontierTicks > 0);
        Assert.True(cold.EvidenceReadTicks > 0);
        Assert.Equal(cold.FrontierTicks, hit.FrontierTicks);
        Assert.Equal(cold.EvidenceReadTicks, hit.EvidenceReadTicks);
        Assert.Equal(1, hit.EvidenceCacheHits);
        Assert.Equal(1, reads);
        Assert.Equal(1, bias.FrontierBuilds);

        clock = 2_001;
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        Assert.Equal(2, reads);
        Assert.Equal(2, bias.BackendReads);
        // The claims of a frontier never change: an expired entry reads again without composing them again.
        Assert.Equal(1, bias.FrontierBuilds);
        var refreshed = bias.ObserveWork();
        Assert.Equal(cold.FrontierTicks, refreshed.FrontierTicks);
        Assert.True(refreshed.EvidenceReadTicks > cold.EvidenceReadTicks);
    }

    [Fact]
    public void SamePositionWithDifferentOrderedLegalFrontierCannotReuseWrongEvidence()
    {
        var fixture = Start("e2e4");
        var bias = Bias(ids => ids.Contains(fixture.Claim) ? Present(ids, fixture.Claim, +10) : Empty());
        ChessMove selected = fixture.Moves[fixture.Index];
        var other = fixture.Moves.First(move => move != selected);
        Assert.Equal(new[] { 80 }, bias.Bonus(fixture.Board, new[] { selected }));
        Assert.Equal(new[] { 0 }, bias.Bonus(fixture.Board, new[] { other }));
        Assert.Equal(new[] { 0, 80 }, bias.Bonus(fixture.Board, new[] { other, selected }));
        Assert.Equal(new[] { 80, 0 }, bias.Bonus(fixture.Board, new[] { selected, other }));
        Assert.Equal(4, bias.BackendReads);
        Assert.Equal(4, bias.FrontierBuilds);
        Assert.Equal(new[] { 0, 80 }, bias.Bonus(fixture.Board, new[] { other, selected }));
        Assert.Equal(4, bias.FrontierBuilds);
        Assert.Equal(1, bias.EvidenceCacheHits);
    }

    [Fact]
    public void ExpiryRefreshesEvidenceWithoutRepeatingTheFrontierOrGrowingTheQueue()
    {
        var fixture = Start("e2e4");
        long clock = 0;
        int reads = 0;
        var bias = new SubstrateRootBias(_ => { reads++; return Empty(); }, clock: () => clock);
        for (int i = 0; i < 100; i++)
        {
            _ = bias.Bonus(fixture.Board, fixture.Moves);
            clock += 2_001;
        }
        Assert.Equal(100, reads);
        Assert.Equal(1, bias.FrontierBuilds);
        Assert.Equal(1, bias.CachedFrontiers);
        Assert.Equal(1, bias.EvictionQueueCount);
    }

    [Fact]
    public void CallerMutationDoesNotChangeTheRetainedOrderedFrontier()
    {
        var fixture = Start("e2e4");
        var bias = Bias(ids => ids.Contains(fixture.Claim) ? Present(ids, fixture.Claim, +10) : Empty());
        var moves = new[] { fixture.Moves[fixture.Index] };
        Assert.Equal(new[] { 80 }, bias.Bonus(fixture.Board, moves));
        moves[0] = fixture.Moves.First(move => move != moves[0]);
        Assert.Equal(new[] { 0 }, bias.Bonus(fixture.Board, moves));
        Assert.Equal(new[] { 80 }, bias.Bonus(fixture.Board, new[] { fixture.Moves[fixture.Index] }));
        Assert.Equal(2, bias.FrontierBuilds);
    }

    [Fact]
    public async Task AFailedOldReadCannotDiscardTheReadThatReplacedIt()
    {
        var fixture = Start("e2e4");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long clock = 0;
        int reads = 0;
        var bias = new SubstrateRootBias(_ =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                throw new InvalidOperationException("old database read failed");
            }
            return Empty();
        }, clock: () => Interlocked.Read(ref clock));
        var old = Task.Run(() => Record.Exception(() => bias.Bonus(fixture.Board, fixture.Moves)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Interlocked.Exchange(ref clock, 2_001);
            _ = bias.Bonus(fixture.Board, fixture.Moves);
        }
        finally { release.Set(); }
        Assert.IsType<InvalidOperationException>(await old);
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        Assert.Equal(2, reads);
        Assert.Equal(1, bias.FrontierBuilds);
        Assert.Equal(1, bias.CachedFrontiers);
    }

    private static SubstrateRootBias Bias(
        Func<IReadOnlyCollection<Hash128>, IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row>> read)
        => new(read, cpPerPoint: 8d, capCp: 150, shrinkK0: 0d);

    private static SubstrateRootBias Shrunk(Fixture fixture, double points, long matches, double deviation)
        => new(ids =>
        {
            Assert.Contains(fixture.Claim, ids);
            return new Dictionary<Hash128, NpgsqlChessTransitionStandings.Row>
            {
                [fixture.Claim] = new(GlickoPriors.NeutralMu / 1e9 + points, deviation, matches),
            };
        });

    // A standing on Native's scale: the rating points above the neutral prior, no deviation, a hundred games.
    private static IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row> Present(
        IReadOnlyCollection<Hash128> requested, Hash128 id, double points)
    {
        Assert.Contains(id, requested);
        return new Dictionary<Hash128, NpgsqlChessTransitionStandings.Row>
        {
            [id] = new(GlickoPriors.NeutralMu / 1e9 + points, Deviation: 0d, Matches: 100),
        };
    }

    private static IReadOnlyDictionary<Hash128, NpgsqlChessTransitionStandings.Row> Empty()
        => new Dictionary<Hash128, NpgsqlChessTransitionStandings.Row>();

    private static Fixture Start(string uci)
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var moves = modality.LegalActions(state);
        int index = Enumerable.Range(0, moves.Count).Single(i => moves[i].ToUci() == uci);
        return new Fixture(state.Board, moves, index,
            ChessNativeRatings.Claim(state, moves[index], modality.Apply(state, moves[index])));
    }

    private sealed record Fixture(Board Board, IReadOnlyList<ChessMove> Moves, int Index, Hash128 Claim);
}
