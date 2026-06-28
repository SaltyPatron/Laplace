using System.Threading;

namespace Laplace.Modality.Chess;

/// <summary>Chooses a move for the side to move (the rng is for diversification / fallback only).</summary>
public delegate ChessMove MoveChooser(ChessState state, Random rng);

/// <summary>
/// Our own engine-vs-engine match harness — no third-party GUI (cutechess) needed. Plays whole games
/// between two <see cref="MoveChooser"/>s under the real rules (<see cref="ChessModality"/>: mate /
/// stalemate / threefold / 50-move / insufficient material; a ply cap adjudicates a draw), alternating
/// colours and diversifying the opening with a few random plies so deterministic engines still play
/// varied games. Reports W/D/L plus the Elo difference and a 95% error margin — the ablation lab that
/// turns each <see cref="EvalTerm"/> overlay into a measured Elo number. Pure C#, no DB/native.
/// </summary>
public static class MatchRunner
{
    public readonly record struct MatchResult(int Games, int AWins, int Draws, int BWins)
    {
        /// <summary>Player A's score in [0,1] (win 1, draw ½).</summary>
        public double Score => Games == 0 ? 0.5 : (AWins + 0.5 * Draws) / Games;

        /// <summary>A's Elo advantage over B (clamped at ±800 for a clean sweep/whitewash).</summary>
        public double EloDiff
        {
            get
            {
                double s = Score;
                if (s <= 0.0) return -800;
                if (s >= 1.0) return 800;
                return Math.Clamp(-400.0 * Math.Log10(1.0 / s - 1.0), -800, 800);
            }
        }

        /// <summary>95% Elo error margin (normal approximation); large when the result is degenerate.</summary>
        public double Margin95
        {
            get
            {
                double s = Score;
                if (Games < 2 || s <= 0.0 || s >= 1.0) return 999;
                double var = (AWins * Sq(1 - s) + Draws * Sq(0.5 - s) + BWins * Sq(0 - s)) / Games;
                double stdErr = Math.Sqrt(var / Games);
                double dEloDs = (400.0 / Math.Log(10)) / (s * (1 - s));
                return 1.96 * dEloDs * stdErr;
            }
        }

        private static double Sq(double x) => x * x;
    }

    /// <summary>
    /// Play <paramref name="games"/> games between A and B, alternating colours (A is White on even game
    /// indices). Each game opens with <paramref name="openingPlies"/> random plies (seeded per game) for
    /// diversity, then the engines take over; a game reaching <paramref name="maxPlies"/> is a draw.
    /// </summary>
    public static MatchResult Play(
        MoveChooser a, MoveChooser b, int games, int maxPlies = 200, int seed = 1, int openingPlies = 4)
    {
        var m = new ChessModality();
        int aWins = 0, draws = 0, bWins = 0;
        for (int g = 0; g < games; g++)
        {
            var rng = new Random(seed + g);
            bool aWhite = (g % 2 == 0);
            int outcome = PlayOne(m, aWhite ? a : b, aWhite ? b : a, maxPlies, rng, openingPlies);
            if (outcome == 2) { draws++; continue; }
            bool whiteWon = outcome == 0;
            bool aWon = aWhite == whiteWon;
            if (aWon) aWins++; else bWins++;
        }
        return new MatchResult(games, aWins, draws, bWins);
    }

    // Returns 0 = White win, 1 = Black win, 2 = draw (terminal or adjudicated at the ply cap).
    private static int PlayOne(
        ChessModality m, MoveChooser white, MoveChooser black, int maxPlies, Random rng, int openingPlies,
        ChessState? start = null)
    {
        var s = start ?? m.Initial();
        for (int ply = 0; ; ply++)
        {
            if (m.Terminal(s) is { } t) return t.IsDraw ? 2 : (t.Winner ?? 2);
            if (ply >= maxPlies) return 2;
            MoveChooser chooser = ply < openingPlies ? RandomChooser
                                : s.Board.WhiteToMove ? white : black;
            s = m.Apply(s, chooser(s, rng));
        }
    }

    /// <summary>Uniform-random legal move — the strength floor and the opening diversifier.</summary>
    public static ChessMove RandomChooser(ChessState s, Random rng)
    {
        var legal = MoveGen.Legal(s.Board);
        return legal[rng.Next(legal.Count)];
    }

    /// <summary>A fixed-depth search player using the given eval overlays (the ablation knob) and an
    /// optional root bias (the substrate seam — guided vs pure measures the graph's Elo contribution).</summary>
    public static MoveChooser Searcher(int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null)
    {
        var search = new Search(terms, bias);
        return (s, rng) => search.Think(s.Board, new Search.Limits(MaxDepth: depth)).BestMove
                           ?? RandomChooser(s, rng);
    }

    /// <summary>A FACTORY for a fresh search player (its own Search/TT) — required for parallel matches,
    /// where sharing one stateful Search across threads would race. A small TT (<paramref name="ttBits"/>)
    /// keeps memory bounded across many concurrent games; a shared <paramref name="bias"/> must be thread-safe.</summary>
    public static Func<MoveChooser> SearcherFactory(
        int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null, int ttBits = 16,
        int[][]? mgPst = null, int[][]? egPst = null)
        => () =>
        {
            var search = new Search(terms, bias, ttBits, mgPst, egPst);
            return (s, rng) => search.Think(s.Board, new Search.Limits(MaxDepth: depth)).BestMove
                               ?? RandomChooser(s, rng);
        };

    /// <summary>Parallel match: each game builds FRESH players from the factories (no shared mutable state)
    /// and runs on the thread pool, up to <paramref name="concurrency"/> at once. Deterministic in the final
    /// tally — each game's seed is fixed — so results reproduce regardless of scheduling.
    /// <para>When <paramref name="openingFens"/> is non-empty each game STARTS from a book position
    /// (rotated by game index, each played once from both colours), instead of <paramref name="openingPlies"/>
    /// random plies — the fair test of corpus knowledge, since the engines begin where the substrate HAS
    /// data. The book FEN replaces the random opening; the ply cap still adjudicates a draw.</para></summary>
    public static MatchResult Play(
        Func<MoveChooser> makeA, Func<MoveChooser> makeB, int games,
        int maxPlies = 200, int seed = 1, int concurrency = 1, int openingPlies = 4,
        IReadOnlyList<string>? openingFens = null)
    {
        bool book = openingFens is { Count: > 0 };
        int aWins = 0, draws = 0, bWins = 0;
        Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency) }, g =>
        {
            var m = new ChessModality();
            var rng = new Random(seed + g);
            var a = makeA();
            var b = makeB();
            bool aWhite = (g % 2 == 0);
            // Pair consecutive games on the same book line (g/2), so both engines play each opening from
            // both sides — removes opening colour bias from the measured Elo.
            var start = book ? m.FromFen(openingFens![(g / 2) % openingFens.Count]) : m.Initial();
            int outcome = PlayOne(m, aWhite ? a : b, aWhite ? b : a, maxPlies, rng,
                                  book ? 0 : openingPlies, start);
            if (outcome == 2) Interlocked.Increment(ref draws);
            else if ((outcome == 0) == aWhite) Interlocked.Increment(ref aWins);
            else Interlocked.Increment(ref bWins);
        });
        return new MatchResult(games, aWins, draws, bWins);
    }
}
