using System.Threading;
using Laplace.Modality;

namespace Laplace.Modality.Chess;

public delegate ChessMove MoveChooser(ChessState state, Random rng);

public static class MatchRunner
{
    public readonly record struct MatchResult(int Games, int AWins, int Draws, int BWins)
    {
        public double Score => Games == 0 ? 0.5 : (AWins + 0.5 * Draws) / Games;

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

    private static int PlayOne(
        ChessModality m, MoveChooser white, MoveChooser black, int maxPlies, Random rng, int openingPlies,
        ChessState? start = null, List<ChessMove>? record = null, CancellationToken ct = default)
        => PlayOneCore(m, white, black, maxPlies, rng, openingPlies, start, record, ct).Outcome;

    private readonly record struct PlayOneResult(int Outcome, RecordedEdge[] Edges, bool Adjudicated);

    private static PlayOneResult PlayOneCore(
        ChessModality m, MoveChooser white, MoveChooser black, int maxPlies, Random rng, int openingPlies,
        ChessState? start = null, List<ChessMove>? record = null, CancellationToken ct = default,
        bool captureEdges = false)
    {
        var s = start ?? m.Initial();
        var subjects = captureEdges ? new List<string>() : null;
        var objects = captureEdges ? new List<string>() : null;
        var movers = captureEdges ? new List<int>() : null;

        for (int ply = 0; ; ply++)
        {
            if (ct.IsCancellationRequested)
                return BuildResult(m, s, subjects, objects, movers, 2, adjudicated: false);

            if (m.Terminal(s) is { } t)
            {
                int code = t.IsDraw ? 2 : (t.Winner ?? 2);
                return BuildResult(m, s, subjects, objects, movers, code, adjudicated: false, terminal: t);
            }

            if (ply >= maxPlies)
                return BuildResult(m, s, subjects, objects, movers, 2, adjudicated: true);

            MoveChooser chooser = ply < openingPlies ? RandomChooser
                                : s.Board.WhiteToMove ? white : black;
            if (captureEdges)
            {
                movers!.Add(m.SideToMove(s));
                subjects!.Add(m.StateKey(s));
            }

            var mv = chooser(s, rng);
            record?.Add(mv);
            s = m.Apply(s, mv);

            if (captureEdges)
                objects!.Add(m.StateKey(s));
        }
    }

    private static PlayOneResult BuildResult(
        ChessModality m, ChessState s, List<string>? subjects, List<string>? objects, List<int>? movers,
        int outcomeCode, bool adjudicated, GameOutcome? terminal = null)
    {
        if (subjects is null || objects is null || movers is null || subjects.Count == 0)
            return new PlayOneResult(outcomeCode, [], adjudicated);

        var gameOutcome = terminal ?? (outcomeCode == 2 ? GameOutcome.Draw : GameOutcome.WonBy(outcomeCode));
        var edges = new RecordedEdge[subjects.Count];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new RecordedEdge(subjects[i], objects[i], null, gameOutcome.ForMover(movers[i]));
        return new PlayOneResult(outcomeCode, edges, adjudicated);
    }

    public static ChessMove RandomChooser(ChessState s, Random rng)
    {
        var legal = MoveGen.Legal(s.Board);
        return legal[rng.Next(legal.Count)];
    }

    public static MoveChooser Searcher(int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null)
    {
        var search = new Search(terms, bias);
        return (s, rng) => search.Think(s.Board, new Search.Limits(MaxDepth: depth)).BestMove
                           ?? RandomChooser(s, rng);
    }

    public static Func<MoveChooser> SearcherFactory(
        int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null, int ttBits = 16,
        int[][]? mgPst = null, int[][]? egPst = null, CancellationToken ct = default)
        => () =>
        {
            var search = new Search(terms, bias, ttBits, mgPst, egPst);
            return (s, rng) => search.Think(s.Board, new Search.Limits(MaxDepth: depth), ct).BestMove
                               ?? RandomChooser(s, rng);
        };

    public static MatchResult Play(
        Func<MoveChooser> makeA, Func<MoveChooser> makeB, int games,
        int maxPlies = 200, int seed = 1, int concurrency = 1, int openingPlies = 4,
        IReadOnlyList<string>? openingFens = null,
        System.Collections.Concurrent.ConcurrentBag<(IReadOnlyList<ChessMove> Moves, int Outcome, string StartFen)>? pgnSink = null,
        IProgress<(int Done, int AWins, int Draws, int BWins)>? progress = null,
        CancellationToken ct = default,
        Action<RecordedEdge[], bool>? recordSink = null)
    {
        bool book = openingFens is { Count: > 0 };
        int aWins = 0, draws = 0, bWins = 0, done = 0;
        int reportEvery = Math.Max(1, games / 500);
        Parallel.For(0, games, new ParallelOptions
        {
            MaxDegreeOfParallelism = ResolveParallelism(concurrency),
            CancellationToken = ct,
        }, g =>
        {
            ct.ThrowIfCancellationRequested();
            var m = new ChessModality();
            var rng = new Random(seed + g);
            var a = makeA();
            var b = makeB();
            bool aWhite = (g % 2 == 0);
            var start = book ? m.FromFen(openingFens![(g / 2) % openingFens.Count]) : m.Initial();
            var pgnMoves = pgnSink is not null ? new List<ChessMove>() : null;
            var played = PlayOneCore(m, aWhite ? a : b, aWhite ? b : a, maxPlies, rng,
                book ? 0 : openingPlies, start, pgnMoves, ct, captureEdges: recordSink is not null);
            int outcome = played.Outcome;
            if (recordSink is not null && played.Edges.Length > 0)
                recordSink(played.Edges, played.Adjudicated);
            if (pgnMoves is not null)
                pgnSink!.Add((pgnMoves, outcome, start.Board.ToFen()));
            if (outcome == 2) Interlocked.Increment(ref draws);
            else if ((outcome == 0) == aWhite) Interlocked.Increment(ref aWins);
            else Interlocked.Increment(ref bWins);
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % reportEvery == 0 || d == games))
                progress.Report((d, Volatile.Read(ref aWins), Volatile.Read(ref draws), Volatile.Read(ref bWins)));
        });
        return new MatchResult(games, aWins, draws, bWins);
    }

    private static int ResolveParallelism(int concurrency)
        => concurrency <= 0 ? -1 : Math.Max(1, concurrency);
}
