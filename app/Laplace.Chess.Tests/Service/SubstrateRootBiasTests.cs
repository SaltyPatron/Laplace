using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class SubstrateRootBiasTests
{
    [Fact]
    public void ExactWitnessedTransitionSteersTheConventionalRoot()
    {
        var fixture = Start("e2e4");
        var bias = Bias((ids, type) => type == ChessVocabulary.MoveType
            ? Present(ids, fixture.TransitionEdge, +10)
            : Empty());

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(80, bonus[fixture.Index]);
        Assert.Equal(1, bias.RootsWithExactEvidence);
        Assert.Equal(1, bias.ExactTransitionSignals);
        Assert.Equal(0, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void TypedPieceFromToEvidenceParticipatesInTheSameRootDecision()
    {
        var fixture = Start("g1f3");
        var bias = Bias((ids, type) => type == ChessVocabulary.OutcomeType
            ? Present(ids, fixture.MoveOutcomeEdge, +12)
            : Empty());

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(96, bonus[fixture.Index]);
        Assert.Equal(0, bias.ExactTransitionSignals);
        Assert.Equal(1, bias.RootsWithMoveEvidence);
        Assert.Equal(1, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void ExactTransitionAndMovePhysicalityAreFusedNotDoubleCounted()
    {
        var fixture = Start("d2d4");
        var bias = Bias((ids, type) => type switch
        {
            var t when t == ChessVocabulary.MoveType =>
                Present(ids, fixture.TransitionEdge, +10),
            var t when t == ChessVocabulary.OutcomeType =>
                Present(ids, fixture.MoveOutcomeEdge, -10),
            _ => Empty(),
        });

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(0, bonus[fixture.Index]);
        Assert.Equal(1, bias.ExactTransitionSignals);
        Assert.Equal(1, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void ReusesFrontierUntilRelevantOnlineEvidenceChanges()
    {
        var fixture = Start("e2e4");
        int reads = 0;
        long version = 0;
        var bias = new SubstrateRootBias(
            (firstIds, firstType, secondIds, secondType) =>
            {
                reads++;
                return (Present(firstIds, fixture.TransitionEdge, +10), Empty());
            },
            cpPerPoint: 8d, capCp: 150, shrinkK0: 0d,
            version: (_, _) => version);

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
        Assert.Equal(1, bias.BackendReads);
        Assert.Equal(1, bias.FrontierBuilds);
        Assert.Equal(1, bias.EvidenceCacheHits);
        long lookups = bias.TransitionPerfcacheHits + bias.TransitionNovelHits + bias.TransitionCompositions;
        Assert.Equal(fixture.Moves.Count, lookups);

        version++;
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        Assert.Equal(2, reads);
        Assert.Equal(2, bias.BackendReads);
        Assert.Equal(1, bias.FrontierBuilds);
        Assert.Equal(lookups, bias.TransitionPerfcacheHits + bias.TransitionNovelHits + bias.TransitionCompositions);
        var refreshed = bias.ObserveWork();
        Assert.Equal(cold.FrontierTicks, refreshed.FrontierTicks);
        Assert.True(refreshed.EvidenceReadTicks > cold.EvidenceReadTicks);
    }

    [Fact]
    public void SamePositionWithDifferentOrderedLegalFrontierCannotReuseWrongEvidence()
    {
        var fixture = Start("e2e4");
        var bias = Bias((ids, type) => type == ChessVocabulary.MoveType
            && ids.Contains(fixture.TransitionEdge) ? Present(ids, fixture.TransitionEdge, +10) : Empty());
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
    public void CrossProcessExpiryRefreshesEvidenceWithoutRepeatingImmutableFrontierOrGrowingQueue()
    {
        var fixture = Start("e2e4");
        long clock = 0;
        int reads = 0;
        var bias = new SubstrateRootBias((_, _, _, _) => { reads++; return (Empty(), Empty()); },
            clock: () => clock);
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
        var bias = Bias((ids, type) => type == ChessVocabulary.MoveType
            && ids.Contains(fixture.TransitionEdge) ? Present(ids, fixture.TransitionEdge, +10) : Empty());
        var moves = new[] { fixture.Moves[fixture.Index] };
        Assert.Equal(new[] { 80 }, bias.Bonus(fixture.Board, moves));
        moves[0] = fixture.Moves.First(move => move != moves[0]);
        Assert.Equal(new[] { 0 }, bias.Bonus(fixture.Board, moves));
        Assert.Equal(new[] { 80 }, bias.Bonus(fixture.Board, new[] { fixture.Moves[fixture.Index] }));
        Assert.Equal(2, bias.FrontierBuilds);
    }

    [Fact]
    public async Task FailedOldObservationCannotDiscardACompletedNewEvidenceGeneration()
    {
        var fixture = Start("e2e4");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long version = 0;
        int reads = 0;
        var bias = new SubstrateRootBias((_, _, _, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                throw new InvalidOperationException("old database observation failed");
            }
            return (Empty(), Empty());
        }, version: (_, _) => Interlocked.Read(ref version));
        var old = Task.Run(() => Record.Exception(() => bias.Bonus(fixture.Board, fixture.Moves)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Interlocked.Increment(ref version);
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
        Func<IReadOnlyCollection<Hash128>, Hash128,
            IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row>> read)
        => new((firstIds, firstType, secondIds, secondType) =>
                (read(firstIds, firstType), read(secondIds, secondType)),
            cpPerPoint: 8d, capCp: 150, shrinkK0: 0d);

    private static IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Present(
        IReadOnlyCollection<Hash128> requested, Hash128 id, double points)
    {
        Assert.Contains(id, requested);
        return new Dictionary<Hash128, NpgsqlConsensusByIds.Row>
        {
            [id] = new(
                GlickoPriors.NeutralMu + points * 1e9,
                Rd: 0d,
                Witnesses: 100d),
        };
    }

    private static IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Empty()
        => new Dictionary<Hash128, NpgsqlConsensusByIds.Row>();

    private static Fixture Start(string uci)
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var moves = modality.LegalActions(state);
        int index = Enumerable.Range(0, moves.Count)
            .Single(i => moves[i].ToUci() == uci);
        ChessMove selected = moves[index];
        Hash128 rootId, moveId, nextId;
        lock (ChessCompose.Gate)
        {
            rootId = ChessCompose.PositionId(state.Board);
            moveId = ChessCompose.MoveId(state.Board.Squares[selected.From], selected);
            nextId = ChessCompose.PositionId(modality.Apply(state, selected).Board);
        }
        return new Fixture(
            state.Board, moves, index,
            ConsensusKeys.EdgeId(rootId, ChessVocabulary.MoveType, nextId),
            ConsensusKeys.EdgeId(
                moveId, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject));
    }

    private sealed record Fixture(
        Board Board, IReadOnlyList<ChessMove> Moves, int Index,
        Hash128 TransitionEdge, Hash128 MoveOutcomeEdge);
}
