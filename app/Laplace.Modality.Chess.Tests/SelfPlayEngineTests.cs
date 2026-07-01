using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class SelfPlayEngineTests
{
    private sealed class FnvAddresser : IContentAddresser
    {
        public Hash128 Address(string s)
        {
            const ulong off = 14695981039346656037UL, prime = 1099511628211UL;
            ulong hi = off, lo = off ^ 0x9E3779B97F4A7C15UL;
            foreach (char c in s)
            {
                hi = (hi ^ c) * prime;
                lo = (lo ^ (uint)(c * 31 + 7)) * prime;
            }
            return new Hash128(hi, lo);
        }
    }

    private sealed class FakeSubstrate : IEdgeRatings, ITurnLearner
    {
        private readonly IContentAddresser _addr;
        private readonly Hash128 _moveType;
        private readonly ConcurrentDictionary<Hash128, (long games, long sumScore)> _edges = new();

        public FakeSubstrate(IContentAddresser addr, Hash128 moveType) { _addr = addr; _moveType = moveType; }

        public long EdgeCount => _edges.Count;

        public Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
        {
            var outv = new double[edgeIds.Count];
            for (int i = 0; i < edgeIds.Count; i++)
                outv[i] = _edges.TryGetValue(edgeIds[i], out var a) ? EffMu(a.games, a.sumScore)
                                                                    : GlickoPriors.UnratedEffMu;
            return Task.FromResult(outv);
        }

        public Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
        {
            foreach (var e in edges)
            {
                var id = ConsensusKeys.EdgeId(_addr.Address(e.SubjectKey), _moveType, _addr.Address(e.ObjectKey));
                long score = e.MoverOutcome switch
                {
                    PlyOutcome.Win => 1_000_000_000L, PlyOutcome.Draw => 500_000_000L, _ => 0L
                };
                _edges.AddOrUpdate(id, (1, score), (_, a) => (a.games + 1, a.sumScore + score));
            }
            return Task.CompletedTask;
        }

        private static double EffMu(long games, long sumScore)
        {
            double mean = (double)sumScore / games / 1e9;
            double rating = GlickoPriors.NeutralMu + (mean - 0.5) * 400_000_000_000d;
            double rd = Math.Max(50_000_000_000d, GlickoPriors.InitialRd - games * 30_000_000_000d);
            return rating - 2d * rd;
        }
    }

    private static ModalityEngine<ChessState, ChessMove> NewEngine(out ChessModality modality, out FakeSubstrate sub)
    {
        modality = new ChessModality();
        var moveType = Hash128.OfCanonical("MOVE");
        var addr = new FnvAddresser();
        sub = new FakeSubstrate(addr, moveType);
        return new ModalityEngine<ChessState, ChessMove>(modality, moveType, addr, sub);
    }

    [Fact]
    public async Task SelfPlay_ProducesLegalCompleteGames()
    {
        var engine = NewEngine(out var modality, out var sub);
        var rng = new Random(12345);

        for (int g = 0; g < 20; g++)
        {
            var played = await engine.PlayGameAsync(modality.Initial(), temperature: 150d, rng, maxPlies: 400);
            await sub.LearnGameAsync(played.Edges);

            Assert.True(played.Plies > 0);
            Assert.Equal(played.Plies, played.Edges.Count);
            if (!played.Adjudicated)
            {
                var final = ReplayToEnd(modality, played);
                Assert.NotNull(modality.Terminal(final));
            }
        }
        Assert.True(sub.EdgeCount > 0);
    }

    [Fact]
    public async Task LearnedWin_ImmediatelyRaisesEffMu_AndIsSelected()
    {
        var engine = NewEngine(out var modality, out var sub);
        var start = modality.Initial();

        var e4 = modality.LegalActions(start).Single(m => m.ToUci() == "e2e4");
        var afterE4 = modality.Apply(start, e4);
        var edge = new RecordedEdge(modality.StateKey(start), modality.StateKey(afterE4), "e2e4", PlyOutcome.Win);

        var before = await engine.ScoreMovesAsync(start);
        var e4Before = before.Single(c => c.Action.ToUci() == "e2e4");
        Assert.False(e4Before.Rated);
        Assert.Equal(GlickoPriors.UnratedEffMu, e4Before.EffMu, 3);

        for (int i = 0; i < 3; i++) await sub.LearnGameAsync(new[] { edge });

        var after = await engine.ScoreMovesAsync(start);
        var e4After = after.Single(c => c.Action.ToUci() == "e2e4");
        Assert.True(e4After.Rated);
        Assert.True(e4After.EffMu > GlickoPriors.UnratedEffMu,
            $"learned-win eff_mu {e4After.EffMu} should exceed unrated prior {GlickoPriors.UnratedEffMu}");

        var chosen = ModalityEngine<ChessState, ChessMove>.Select(after, temperature: 0d, new Random(1));
        Assert.Equal("e2e4", chosen.Action.ToUci());
    }

    [Fact]
    public async Task LearnedLoss_DropsEffMuBelowPrior()
    {
        var engine = NewEngine(out var modality, out var sub);
        var start = modality.Initial();
        var a3 = modality.LegalActions(start).Single(m => m.ToUci() == "a2a3");
        var afterA3 = modality.Apply(start, a3);
        var edge = new RecordedEdge(modality.StateKey(start), modality.StateKey(afterA3), "a2a3", PlyOutcome.Loss);

        for (int i = 0; i < 3; i++) await sub.LearnGameAsync(new[] { edge });

        var scored = await engine.ScoreMovesAsync(start);
        var a3After = scored.Single(c => c.Action.ToUci() == "a2a3");
        Assert.True(a3After.Rated);
        Assert.True(a3After.EffMu < GlickoPriors.UnratedEffMu,
            $"learned-loss eff_mu {a3After.EffMu} should fall below unrated prior {GlickoPriors.UnratedEffMu}");
    }

    private static ChessState ReplayToEnd(ChessModality modality, PlayedGame<ChessMove> played)
    {
        var state = modality.Initial();
        foreach (var e in played.Edges)
        {
            var mv = modality.LegalActions(state).Single(m => m.ToUci() == e.MoveKey);
            state = modality.Apply(state, mv);
        }
        return state;
    }
}
