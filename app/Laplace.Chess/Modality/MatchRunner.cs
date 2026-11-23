using System.Threading;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Modality.Chess;

public delegate ChessMove MoveChooser(ChessState state, Random rng);
public readonly record struct MatchPgnGame(
    IReadOnlyList<ChessMove> Moves, int Outcome, string StartFen,
    string White, string Black);

public static class MatchRunner
{
    public readonly record struct MatchResult(int Games, int AWins, int Draws, int BWins)
    {
        public int Incomplete { get; init; }
        public int Decided => AWins + Draws + BWins;
        public double Score => Decided == 0 ? 0.5 : (AWins + 0.5 * Draws) / Decided;

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
                int decided = Decided;
                if (decided < 2 || s <= 0.0 || s >= 1.0) return 999;
                double var = (AWins * Sq(1 - s) + Draws * Sq(0.5 - s) + BWins * Sq(0 - s)) / decided;
                double stdErr = Math.Sqrt(var / decided);
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
        int aWins = 0, draws = 0, bWins = 0, incomplete = 0;
        for (int g = 0; g < games; g++)
        {
            var rng = new Random(seed + g);
            bool aWhite = (g % 2 == 0);
            int outcome = PlayOne(m, aWhite ? a : b, aWhite ? b : a, maxPlies, rng, openingPlies);
            if (outcome < 0 || outcome > 2) { incomplete++; continue; }
            if (outcome == 2) { draws++; continue; }
            bool whiteWon = outcome == 0;
            bool aWon = aWhite == whiteWon;
            if (aWon) aWins++; else bWins++;
        }
        return new MatchResult(games, aWins, draws, bWins) { Incomplete = incomplete };
    }

    private static int PlayOne(
        ChessModality m, MoveChooser white, MoveChooser black, int maxPlies, Random rng, int openingPlies,
        ChessState? start = null, List<ChessMove>? record = null, CancellationToken ct = default)
        => PlayOneCore(m, white, black, maxPlies, rng, openingPlies, start, record, ct).Outcome;

    private readonly record struct RecordedPlyInfo(int Ply, string FromKey, string ToKey, string Uci);
    private readonly record struct PlayOneResult(int Outcome, RecordedEdge[] Edges, bool Adjudicated, List<RecordedPlyInfo>? Plies);

    private static PlayOneResult PlayOneCore(
        ChessModality m, MoveChooser white, MoveChooser black, int maxPlies, Random rng, int openingPlies,
        ChessState? start = null, List<ChessMove>? record = null, CancellationToken ct = default,
        bool captureEdges = false,
        bool capturePlies = false,
        Action<int, string, string>? onPly = null)
    {
        var s = start ?? m.Initial();
        var subjects = captureEdges ? new List<string>() : null;
        var objects = captureEdges ? new List<string>() : null;
        var movers = captureEdges ? new List<int>() : null;
        var plies = capturePlies ? new List<RecordedPlyInfo>() : null;
        int plyNum = 0;

        for (int ply = 0; ; ply++)
        {
            if (ct.IsCancellationRequested)
                return BuildResult(subjects, objects, movers, plies, -1, adjudicated: false);

            if (m.Terminal(s) is { } t)
            {
                int code = t.IsUnknown ? -1 : t.IsDraw ? 2 : (t.Winner ?? -1);
                return BuildResult(subjects, objects, movers, plies, code, adjudicated: false, terminal: t);
            }

            if (ply >= maxPlies)
                return BuildResult(subjects, objects, movers, plies, -1, adjudicated: true);

            MoveChooser chooser = ply < openingPlies ? RandomChooser
                                : s.Board.WhiteToMove ? white : black;
            string? fromKey = (captureEdges || capturePlies) ? m.StateKey(s) : null;
            if (captureEdges)
            {
                movers!.Add(m.SideToMove(s));
                subjects!.Add(fromKey!);
            }

            var mv = chooser(s, rng);
            record?.Add(mv);
            s = m.Apply(s, mv);

            if (captureEdges || capturePlies || onPly is not null)
            {
                string toKey = m.StateKey(s);
                string uci = mv.ToUci();
                if (captureEdges) objects!.Add(toKey);
                plyNum++;
                if (capturePlies) plies!.Add(new RecordedPlyInfo(plyNum, fromKey!, toKey, uci));
                onPly?.Invoke(plyNum, uci, toKey);
            }
        }
    }

    private static PlayOneResult BuildResult(
        List<string>? subjects, List<string>? objects, List<int>? movers, List<RecordedPlyInfo>? plies,
        int outcomeCode, bool adjudicated, GameOutcome? terminal = null)
    {
        if (subjects is null || objects is null || movers is null || subjects.Count == 0)
            return new PlayOneResult(outcomeCode, [], adjudicated, plies);

        var gameOutcome = terminal ?? (outcomeCode switch
        {
            0 => GameOutcome.WonBy(0),
            1 => GameOutcome.WonBy(1),
            2 => GameOutcome.Draw,
            _ => GameOutcome.Unknown,
        });
        var edges = new RecordedEdge[subjects.Count];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new RecordedEdge(subjects[i], objects[i], null, gameOutcome.ForMover(movers[i]));
        return new PlayOneResult(outcomeCode, edges, adjudicated, plies);
    }

    public static ChessMove RandomChooser(ChessState s, Random rng)
    {
        var legal = MoveGen.Legal(s.Board);
        return legal[rng.Next(legal.Count)];
    }

    public static MoveChooser Searcher(int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null)
    {
        var search = new Search(terms, bias);
        return (s, rng) => search.Think(
            s, new Search.Limits(MaxDepth: depth)).BestMove!.Value;
    }

    public static Func<MoveChooser> SearcherFactory(
        int depth, EvalTerm terms = EvalTerm.All, IRootBias? bias = null, int ttBits = 16,
        int[][]? mgPst = null, int[][]? egPst = null, CancellationToken ct = default,
        ISearchPositionEvaluator? positionEvaluator = null,
        Func<Board, SearchTablebaseVerdict?>? tablebase = null)
        => () =>
        {
            var search = new Search(
                terms, bias, ttBits, mgPst, egPst, positionEvaluator, tablebase);
            return (s, rng) => search.Think(
                s, new Search.Limits(MaxDepth: depth), ct).BestMove!.Value;
        };

    private sealed record BufferedLiveGame(
        Hash128 GameId,
        string? WhiteName,
        string? BlackName,
        string StartFen,
        string? ExternalGameId,
        List<RecordedPlyInfo> Plies,
        GameOutcome Outcome,
        bool Adjudicated);

    public static MatchResult Play(
        Func<MoveChooser> makeA, Func<MoveChooser> makeB, int games,
        int maxPlies = 200, int seed = 1, int concurrency = 1, int openingPlies = 4,
        IReadOnlyList<string>? openingFens = null,
        System.Collections.Concurrent.ConcurrentBag<MatchPgnGame>? pgnSink = null,
        IProgress<(int Done, int AWins, int Draws, int BWins)>? progress = null,
        CancellationToken ct = default,
        Action<RecordedEdge[], bool>? recordSink = null,
        ChessLiveGameHost? liveHost = null,
        string? liveLearnContext = null,
        Action<int, int, string, string>? onPly = null,
        string? aPlayerName = null,
        string? bPlayerName = null,
        string? eventName = null,
        string? externalIdPrefix = null)
    {
        bool book = openingFens is { Count: > 0 };
        int aWins = 0, draws = 0, bWins = 0, incomplete = 0, done = 0;
        int reportEvery = Math.Max(1, games / 500);
        _ = liveLearnContext;

        var bufferedLive = liveHost is null ? null : new BufferedLiveGame?[games];

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
                book ? 0 : openingPlies, start, pgnMoves, ct,
                captureEdges: recordSink is not null,
                capturePlies: liveHost is not null,
                onPly: onPly is null ? null : (ply, uci, toKey) => onPly(g, ply, uci, toKey));
            int outcome = played.Outcome;
            if (recordSink is not null && played.Edges.Length > 0)
                recordSink(played.Edges, played.Adjudicated);
            if (liveHost is not null)
            {
                var terminal = ToGameOutcome(outcome, played.Adjudicated);
                bufferedLive![g] = new BufferedLiveGame(
                    ChessLiveGameHost.SessionHandle(Guid.NewGuid()),
                    aWhite ? aPlayerName : bPlayerName,
                    aWhite ? bPlayerName : aPlayerName,
                    start.Board.ToFen(),
                    externalIdPrefix is null ? null : $"{externalIdPrefix}/{g}",
                    played.Plies ?? [],
                    terminal,
                    played.Adjudicated);
            }
            if (pgnMoves is not null)
                pgnSink!.Add(new MatchPgnGame(
                    pgnMoves, outcome, start.Board.ToFen(),
                    aWhite ? aPlayerName ?? "A" : bPlayerName ?? "B",
                    aWhite ? bPlayerName ?? "B" : aPlayerName ?? "A"));
            if (outcome == 2) Interlocked.Increment(ref draws);
            else if (outcome is 0 or 1)
            {
                if ((outcome == 0) == aWhite) Interlocked.Increment(ref aWins);
                else Interlocked.Increment(ref bWins);
            }
            else Interlocked.Increment(ref incomplete);
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % reportEvery == 0 || d == games))
                progress.Report((d, Volatile.Read(ref aWins), Volatile.Read(ref draws), Volatile.Read(ref bWins)));
        });

        // Deterministic post-measurement learning order. No game from this match can affect
        // another game's decision, regardless of concurrency or which worker finished first.
        if (liveHost is not null && bufferedLive is not null)
        {
            Task.Run(() => RecordBufferedGamesAsync(liveHost, bufferedLive, eventName, ct), ct)
                .GetAwaiter().GetResult();
        }

        return new MatchResult(games, aWins, draws, bWins) { Incomplete = incomplete };
    }

    public static async Task<MatchResult> PlayAsync(
        Func<MoveChooser> makeA, Func<MoveChooser> makeB, int games,
        int maxPlies = 200, int seed = 1, int concurrency = 1, int openingPlies = 4,
        IReadOnlyList<string>? openingFens = null,
        System.Collections.Concurrent.ConcurrentBag<MatchPgnGame>? pgnSink = null,
        IProgress<(int Done, int AWins, int Draws, int BWins)>? progress = null,
        CancellationToken ct = default,
        Action<RecordedEdge[], bool>? recordSink = null,
        ChessLiveGameHost? liveHost = null,
        string? liveLearnContext = null,
        Action<int, int, string, string>? onPly = null,
        string? aPlayerName = null,
        string? bPlayerName = null,
        string? eventName = null,
        string? externalIdPrefix = null)
    {
        bool book = openingFens is { Count: > 0 };
        int aWins = 0, draws = 0, bWins = 0, incomplete = 0, done = 0;
        int reportEvery = Math.Max(1, games / 500);
        _ = liveLearnContext;

        var deferredLive = liveHost is null
            ? null
            : new (Hash128 GameId, GameOutcome Outcome, bool Adjudicated)?[games];
        string dateTag = DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture);

        await Parallel.ForEachAsync(Enumerable.Range(0, games), new ParallelOptions
        {
            MaxDegreeOfParallelism = ResolveParallelism(concurrency),
            CancellationToken = ct,
        }, async (g, token) =>
        {
            token.ThrowIfCancellationRequested();
            var m = new ChessModality();
            var rng = new Random(seed + g);
            var a = makeA();
            var b = makeB();
            bool aWhite = (g % 2 == 0);
            var start = book ? m.FromFen(openingFens![(g / 2) % openingFens.Count]) : m.Initial();
            var pgnMoves = pgnSink is not null ? new List<ChessMove>() : null;

            Hash128? gameId = liveHost is not null ? ChessLiveGameHost.SessionHandle(Guid.NewGuid()) : null;
            if (liveHost is not null && gameId is { } gidOpen)
            {
                await liveHost.OpenGameAsync(
                    gidOpen,
                    aWhite ? aPlayerName : bPlayerName,
                    aWhite ? bPlayerName : aPlayerName,
                    new ChessLiveGameMetadata(
                        Event: eventName,
                        Site: "Laplace Chess Lab",
                        Date: dateTag,
                        StartFen: start.Board.ToFen(),
                        ExternalGameId: externalIdPrefix is null ? null : $"{externalIdPrefix}/{g}"),
                    token).ConfigureAwait(false);
            }

            var played = PlayOneCore(m, aWhite ? a : b, aWhite ? b : a, maxPlies, rng,
                book ? 0 : openingPlies, start, pgnMoves, token,
                captureEdges: recordSink is not null,
                capturePlies: liveHost is not null,
                onPly: onPly is null ? null : (ply, uci, toKey) => onPly(g, ply, uci, toKey));

            if (liveHost is not null && gameId is { } gidPlies && played.Plies is { Count: > 0 } recordedPlies)
            {
                foreach (var p in recordedPlies)
                    await liveHost.RecordPlyAsync(gidPlies, p.Ply, p.FromKey, p.ToKey, p.Uci, token).ConfigureAwait(false);
            }

            int outcome = played.Outcome;
            if (recordSink is not null && played.Edges.Length > 0)
                recordSink(played.Edges, played.Adjudicated);
            if (liveHost is not null && gameId is { } gidDone)
                deferredLive![g] = (gidDone, ToGameOutcome(outcome, played.Adjudicated), played.Adjudicated);
            if (pgnMoves is not null)
                pgnSink!.Add(new MatchPgnGame(
                    pgnMoves, outcome, start.Board.ToFen(),
                    aWhite ? aPlayerName ?? "A" : bPlayerName ?? "B",
                    aWhite ? bPlayerName ?? "B" : aPlayerName ?? "A"));
            if (outcome == 2) Interlocked.Increment(ref draws);
            else if (outcome is 0 or 1)
            {
                if ((outcome == 0) == aWhite) Interlocked.Increment(ref aWins);
                else Interlocked.Increment(ref bWins);
            }
            else Interlocked.Increment(ref incomplete);
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % reportEvery == 0 || d == games))
                progress.Report((d, Volatile.Read(ref aWins), Volatile.Read(ref draws), Volatile.Read(ref bWins)));
        }).ConfigureAwait(false);

        if (liveHost is not null && deferredLive is not null)
        {
            for (int g = 0; g < deferredLive.Length; g++)
            {
                if (deferredLive[g] is not { } completed) continue;
                await liveHost.CompleteGameAsync(
                    completed.GameId, completed.Outcome, completed.Adjudicated, ct).ConfigureAwait(false);
            }
        }

        return new MatchResult(games, aWins, draws, bWins) { Incomplete = incomplete };
    }

    private static async Task RecordBufferedGamesAsync(
        ChessLiveGameHost liveHost,
        BufferedLiveGame?[] buffered,
        string? eventName,
        CancellationToken ct)
    {
        string dateTag = DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var g in buffered)
        {
            if (g is null) continue;
            await liveHost.OpenGameAsync(
                g.GameId, g.WhiteName, g.BlackName,
                new ChessLiveGameMetadata(
                    Event: eventName,
                    Site: "Laplace Chess Lab",
                    Date: dateTag,
                    StartFen: g.StartFen,
                    ExternalGameId: g.ExternalGameId),
                ct).ConfigureAwait(false);
            foreach (var p in g.Plies)
                await liveHost.RecordPlyAsync(g.GameId, p.Ply, p.FromKey, p.ToKey, p.Uci, ct).ConfigureAwait(false);
            await liveHost.CompleteGameAsync(g.GameId, g.Outcome, g.Adjudicated, ct).ConfigureAwait(false);
        }
    }

    private static GameOutcome ToGameOutcome(int outcome, bool adjudicated) =>
        adjudicated || outcome < 0 || outcome > 2
            ? GameOutcome.Unknown
            : outcome == 2 ? GameOutcome.Draw : GameOutcome.WonBy(outcome);

    private static int ResolveParallelism(int concurrency)
        => concurrency <= 0 ? -1 : Math.Max(1, concurrency);
}
