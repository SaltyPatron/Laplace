using System.Linq;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// CALCULATED layer. Derives positions / geometry / move edges / motifs / opening classification /
// consensus by REPLAYING a game's witnessed movetext. Pure deterministic function of the witnessed
// inputs (movetext + start FEN + per-ply annotation tokens the recorder stored). Emitted under the
// analysis source and stamped ANALYZED_AT=Version so the analyzer scan skips already-derived games.
//
// This is the logic that used to run inline during ingest (ChessPgnDecomposer.AppendGame) — moved
// here so ingest is pure recording. The scan/watermark runner that reads the witnessed layer from
// the DB and calls DeriveGame per un-analyzed game is the remaining piece (see .scratchpad/08).
public static class ChessAnalyze
{
    public const int Version = 1;
    public static Hash128 SourceId => ChessVocabulary.AnalysisSourceId;

    private const double MoveWeight = 0.7;
    private const double MetaWeight = 0.7;
    private const double EvalWeight = 0.55;
    private const long EvalGames = 2;

    // Entry point for the analyzer decomposer: assemble DeriveGame's inputs from a parsed game
    // (the witnessed content), derive, and stamp the (game, version) marker the scan probes.
    internal static void DeriveFromParsed(SubstrateChangeBuilder b, ChessPgnDecomposer.ParsedGame parsed)
    {
        var (gameText, walk, moves, result, gameId) = parsed;

        string whiteName = PgnGames.TagStr(gameText, "White");
        string blackName = PgnGames.TagStr(gameText, "Black");
        Hash128? wp = ValidName(whiteName) ? ChessVocabulary.PlayerId(whiteName) : null;
        Hash128? bp = ValidName(blackName) ? ChessVocabulary.PlayerId(blackName) : null;

        string? startFen = PgnGames.TagStr(gameText, "SetUp") == "1"
            ? NullIfBlank(PgnGames.TagStr(gameText, "FEN")) : null;

        int mc = moves.Count;
        var clockTokens = PgnClocks.ClockTokens(gameText, mc);
        var clocks = clockTokens is not null
            ? clockTokens.Select(ParseClockSeconds).ToArray()
            : System.Array.Empty<double>();
        double medianDrop = PgnClocks.MedianDrop(clocks);
        var evalTokens = PgnEvals.EvalTokens(gameText, mc);
        var evals = evalTokens is not null
            ? evalTokens.Select(PgnEvals.ParseToken).ToArray()
            : PgnEvals.Centipawns(gameText, mc);

        var qualityTokens = new string?[walk.Mainline.Count];
        for (int i = 0; i < walk.Mainline.Count; i++)
            qualityTokens[i] = MoveQuality.FromStream(walk.Mainline[i]);

        // Opt-in: run our OWN engine eval per position (a higher-trust witness competing with the
        // PGN's eval on the same position). Off by default (structural derivation only); set
        // LAPLACE_CHESS_ANALYZE_DEPTH>0 to dedicate compute to it. This is "target given games,
        // dedicate compute to analysis."
        int engineDepth = int.TryParse(
            System.Environment.GetEnvironmentVariable("LAPLACE_CHESS_ANALYZE_DEPTH"), out var d) ? d : 0;

        DeriveGame(b, gameId, result, moves, startFen, wp, bp,
                   clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth);

        // Fast-probe watermark anchor: this (game, version) is now derived; the scan skips it next run.
        b.AddEntity(ChessVocabulary.AnalysisMarkerId(gameId, Version), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, SourceId);
    }

    private static bool ValidName(string n) => !string.IsNullOrWhiteSpace(n) && n != "?";
    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static double ParseClockSeconds(string t)
    {
        var p = t.Split(':');
        return p.Length >= 3
            ? int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60
              + double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    // Derive one game's calculated layer from its witnessed inputs. `sans` is the replayed movetext;
    // token arrays are indexed by ply (sparse allowed); `evals` are centipawns (mover POV pre-sign).
    public static void DeriveGame(
        SubstrateChangeBuilder b, Hash128 gameId, GameOutcome result,
        IReadOnlyList<string> sans, string? startFen,
        Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth = 0)
    {
        var m = new ChessModality();
        var (initial, standardStart) = InitialState(startFen, m);

        // Opening classification + named-trap motif only make sense from the standard array.
        if (standardStart) ClassifyOpening(b, gameId, sans, m);

        AppendGame(b, m, initial, sans, result, whitePlayer, blackPlayer, gameId,
                   clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth);

        // Watermark: this game is now derived at the current analysis version.
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddAttestation(NativeAttestation.Categorical(
                gameId, "ANALYZED_AT", vId, SourceId, null, ChessVocabulary.Trust));
    }

    public static (ChessState Initial, bool StandardStart) InitialState(string? startFen, ChessModality m)
    {
        if (string.IsNullOrWhiteSpace(startFen)) return (m.Initial(), true);
        try { return (m.FromFen(startFen), false); }
        catch (FormatException) { return (m.Initial(), true); }
    }

    private static void ClassifyOpening(
        SubstrateChangeBuilder b, Hash128 gameId, IReadOnlyList<string> sans, ChessModality m)
    {
        var src = SourceId;
        var classified = OpeningClassifier.Classify(sans, m);
        if (classified.Eco is { } eco)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", eco, MoveWeight, src);
        if (classified.Name is { } name)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", name, MoveWeight, src);
        if (ChessMotifs.DetectNamedTrap(sans) is { } motif)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_MOTIF", motif, MoveWeight, src);
    }

    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, ChessState initial, IReadOnlyList<string> sans,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, Hash128 gameId,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth)
    {
        var src = SourceId;
        bool mate = sans.Count > 0 && sans[^1].IndexOf('#') >= 0;
        int? winner = result.IsDraw ? null : result.Winner;
        // Reused across plies so the TT warms; only built when engine-eval is requested.
        var engine = engineDepth > 0 ? new Search(EvalTerm.All) : null;

        var state = initial;
        for (int ply = 0; ply < sans.Count; ply++)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), sans[ply]);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            string fromKey = m.StateKey(state);

            // Our OWN eval (high-trust ChessAnalysis witness) competes on (position, HAS_EVAL) with
            // the PGN's eval (lower-trust EvalPgn, emitted below). Score is side-to-move cp.
            if (engine is not null)
            {
                int ourCp = engine.Think(state.Board, new Search.Limits(MaxDepth: engineDepth)).Score;
                ChessGraph.AppendEval(b, fromKey, ourCp, games: 1, witnessWeight: 0.9, src, gameId);
            }

            long games = 1;
            if (mate && winner == mover) games += 1;

            ChessGraph.AppendMoveEdge(
                b, fromKey, m.StateKey(next), result.ForMover(mover), games, MoveWeight,
                sourceId: src,
                moverPlayerId: mover == 0 ? whitePlayer : blackPlayer,
                contextId: gameId,
                ply: ply + 1);

            foreach (var tag in ChessMotifs.DetectAtPly(state.Board, mv.Value, next.Board))
                ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_MOTIF", tag, MoveWeight, src);

            string? clk = Tok(clockTokens, ply);
            if (clk is not null)
            {
                ChessGraph.AppendClock(b, fromKey, clk, MetaWeight, src, gameId);
                if (clocks.Length > 0)
                {
                    double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
                    ChessGraph.AppendThinkClass(b, fromKey, ChessCanonical.ThinkClass(tf), MetaWeight, src, gameId);
                }
            }

            string? evTok = Tok(evalTokens, ply);
            if (evTok is not null)
                ChessGraph.AppendEvalToken(b, fromKey, evTok, MetaWeight, ChessVocabulary.EvalPgnSourceId, gameId);

            if (evals is not null && ply < evals.Length)
            {
                int cp = mover == 0 ? evals[ply] : -evals[ply];
                ChessGraph.AppendEval(b, fromKey, cp, EvalGames, EvalWeight, ChessVocabulary.EvalPgnSourceId, gameId);
            }

            string? q = Tok(qualityTokens, ply);
            if (q is not null)
                ChessGraph.AppendMoveQuality(b, fromKey, q, 1, MoveWeight * 0.5, src, gameId);

            state = next;
        }
    }

    private static string? Tok(string?[]? arr, int i)
        => arr is not null && i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : null;
}
