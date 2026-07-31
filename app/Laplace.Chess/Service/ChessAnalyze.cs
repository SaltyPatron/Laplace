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
// Scan driver: <see cref="ChessWitnessHydrator"/> reads witnessed attestations from Postgres.
// PGN path is legacy bootstrap only when an explicit file path is passed.
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
    internal static void DeriveFromParsed(SubstrateChangeBuilder b, ChessGameRecord parsed)
        => DeriveFromWitnessed(b, WitnessedFromParsed(parsed));

    /// <summary>Derive from substrate-hydrated witnessed inputs (no PGN re-parse).</summary>
    internal static void DeriveFromWitnessed(SubstrateChangeBuilder b, ChessWitnessedGame witnessed, int engineDepth = 0)
    {
        var (lineId, eventId, moves, result, wp, bp, startFen, clockTokens, evalTokens, qualityTokens, spentSeconds) = witnessed;

        var clocks = clockTokens is not null
            ? clockTokens.Select(t => t is null ? 0.0 : ParseClockSeconds(t)).ToArray()
            : System.Array.Empty<double>();
        double medianDrop = PgnClocks.MedianDrop(clocks);
        var evals = evalTokens is not null
            ? evalTokens.Select(t => t is null ? 0 : PgnEvals.ParseToken(t)).ToArray()
            : null;

        DeriveGame(b, lineId, eventId, result, moves, startFen, wp, bp,
                   clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth,
                   spentSeconds);

        // GH #736: the analyzer's unit is the PLAYING — its deposits carry this playing's
        // outcome/clock/think/eval contexts — so the skip marker is per EVENT. Two playings
        // of one line each fold their own testimony; neither is skipped.
        b.AddEntity(ChessVocabulary.AnalysisMarkerId(eventId, Version), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, SourceId);
    }

    internal static ChessWitnessedGame WitnessedFromParsed(ChessGameRecord parsed)
    {
        var (gameText, moves, result, lineId, eventId) = parsed;
        var walk = parsed.Walk;
        string whiteName = PgnGames.TagStr(gameText, "White");
        string blackName = PgnGames.TagStr(gameText, "Black");
        Hash128? wp = ValidName(whiteName) ? ChessVocabulary.PlayerId(whiteName) : null;
        Hash128? bp = ValidName(blackName) ? ChessVocabulary.PlayerId(blackName) : null;
        string? startFen = PgnGames.TagStr(gameText, "SetUp") == "1"
            ? NullIfBlank(PgnGames.TagStr(gameText, "FEN")) : null;

        int mc = moves.Count;
        var clockTokens = PgnClocks.ClockTokens(gameText, mc);
        // cutechess dialect: no remaining-clock tokens, but per-move spent time (GH #494).
        var spentSeconds = clockTokens is null ? PgnClocks.SpentSeconds(gameText, mc) : null;
        var evalTokens = PgnEvals.EvalTokens(gameText, mc);
        var qualityTokens = new string?[walk.Mainline.Count];
        for (int i = 0; i < walk.Mainline.Count; i++)
            qualityTokens[i] = MoveQuality.FromStream(walk.Mainline[i]);

        return new ChessWitnessedGame(
            lineId, eventId, moves, result, wp, bp, startFen, clockTokens, evalTokens, qualityTokens,
            spentSeconds);
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

    // Derive one playing's calculated layer from its witnessed inputs. `sans` is the replayed
    // movetext; token arrays are indexed by ply (sparse allowed); `evals` are centipawns (mover
    // POV pre-sign). GH #736: line-grain facts (opening/motif) subject onto the LINE; per-playing
    // testimony carries ctx = the EVENT.
    public static void DeriveGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, GameOutcome result,
        IReadOnlyList<string> sans, string? startFen,
        Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth = 0, double[]? spentSeconds = null)
    {
        var m = new ChessModality();
        var (initial, standardStart) = InitialState(startFen, m);

        // Opening classification + named-trap motif only make sense from the standard array.
        if (standardStart) ClassifyOpening(b, lineId, sans, m);

        AppendGame(b, m, initial, sans, result, whitePlayer, blackPlayer, lineId, eventId,
                   clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth,
                   spentSeconds, standardStart);

        // Watermark: this playing is now derived at the current analysis version.
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddAttestation(NativeAttestation.Categorical(
                eventId, "ANALYZED_AT", vId, SourceId, null, ChessVocabulary.Trust));
    }

    public static (ChessState Initial, bool StandardStart) InitialState(string? startFen, ChessModality m)
    {
        if (string.IsNullOrWhiteSpace(startFen)) return (m.Initial(), true);
        try { return (m.FromFen(startFen), false); }
        catch (FormatException) { return (m.Initial(), true); }
    }

    // Line-grain classification: every playing is a witness that the LINE is that opening /
    // exhibits that trap, so the subject is the line and re-derivation per event is the
    // intended duplication (each playing corroborates the categorical cell).
    private static void ClassifyOpening(
        SubstrateChangeBuilder b, Hash128 lineId, IReadOnlyList<string> sans, ChessModality m)
    {
        var src = SourceId;
        var classified = OpeningClassifier.Classify(sans, m);
        if (classified.Eco is { } eco)
            ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_ECO", eco, MoveWeight, src);
        if (classified.Name is { } name)
            ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_OPENING", name, MoveWeight, src);
        if (ChessMotifs.DetectNamedTrap(sans) is { } motif)
            ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_MOTIF", motif, MoveWeight, src);
    }

    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, ChessState initial, IReadOnlyList<string> sans,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, Hash128 lineId, Hash128 eventId,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth, double[]? spentSeconds = null, bool standardStart = true)
    {
        var src = SourceId;
        double medianSpent = PgnClocks.MedianSpent(spentSeconds);
        bool mate = sans.Count > 0 && sans[^1].IndexOf('#') >= 0;
        int? winner = result.IsDraw ? null : result.Winner;
        // Reused across plies so the TT warms; only built when engine-eval is requested.
        var engine = engineDepth > 0 ? new Search(EvalTerm.All) : null;

        var state = initial;
        // Each distinct position is composed + staged ONCE per ply (the builder dedups by id,
        // so re-staging in every Append* helper was pure waste). This ply's `to` nodes carry
        // forward as the next ply's `from`, so its StateKey/compose is never redone either.
        ChessComposed? carried = null;
        // The game's own line, collected as we walk it: start position then one vertex per ply.
        // Free — these are the nodes the loop already composed — and it becomes the game
        // trajectory below.
        var line = new List<ChessNode>(sans.Count + 1);
        // The replay window for the multi-ply motif detectors. Free: Apply clones a fresh
        // Board per ply, so these are references to boards the loop already built.
        var boards = new List<Board>(sans.Count + 1) { initial.Board };
        var played = new List<ChessMove>(sans.Count);

        // Move generation is ~46% of analyze time, and ChessModality.LegalActions allocates a
        // fresh pseudo AND legal list on EVERY ply — ~12.8M plies per corpus, so ~25M list
        // allocations that exist for microseconds. #651 added buffered MoveGen overloads for
        // exactly this and the ingest hot loop never adopted them. Two buffers per GAME,
        // reused across its plies, instead of two per ply.
        var pseudoBuf = new List<ChessMove>(64);
        var legalBuf = new List<ChessMove>(64);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            MoveGen.Legal(state.Board, pseudoBuf, legalBuf);
            var mv = San.Resolve(state.Board, legalBuf, sans[ply]);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            var from = carried ?? ChessGraph.EmitComposed(b, m.StateKey(state), src);
            var to = ChessGraph.EmitComposed(b, m.StateKey(next), src);
            if (line.Count == 0) line.Add(from.Position);
            line.Add(to.Position);
            boards.Add(next.Board);
            played.Add(mv.Value);

            // Our OWN eval (high-trust ChessAnalysis witness) competes on (position, HAS_EVAL) with
            // the PGN's eval (lower-trust EvalPgn, emitted below). Score is side-to-move cp.
            if (engine is not null)
            {
                int ourCp = engine.Think(state.Board, new Search.Limits(MaxDepth: engineDepth)).Score;
                ChessGraph.AppendEval(b, from, ourCp, games: 1, witnessWeight: 0.9, src, eventId);
            }

            long games = 1;
            if (mate && winner == mover) games += 1;

            ChessGraph.AppendMoveEdge(
                b, from, to, result.ForMover(mover), games, MoveWeight,
                sourceId: src,
                contextId: eventId);


            string? clk = Tok(clockTokens, ply);
            if (clk is not null)
            {
                ChessGraph.AppendClock(b, from.Position.Id, clk, MetaWeight, src, eventId);
                if (clocks.Length > 0)
                {
                    double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
                    ChessGraph.AppendThinkClass(b, from.Position.Id, ChessCanonical.ThinkClass(tf), MetaWeight, src, eventId);
                }
            }
            else if (spentSeconds is not null && medianSpent > 0)
            {
                // cutechess dialect (GH #494): per-move spent time is the think signal directly.
                // No HAS_CLOCK deposit — the source never asserted a remaining clock.
                double tf = PgnClocks.ThinkFactorFromSpent(spentSeconds, medianSpent, ply);
                ChessGraph.AppendThinkClass(b, from.Position.Id, ChessCanonical.ThinkClass(tf), MetaWeight, src, eventId);
            }

            string? evTok = Tok(evalTokens, ply);
            if (evTok is not null)
                ChessGraph.AppendEvalToken(b, from.Position.Id, evTok, MetaWeight, ChessVocabulary.EvalPgnSourceId, eventId);

            if (evals is not null && ply < evals.Length)
            {
                int cp = mover == 0 ? evals[ply] : -evals[ply];
                ChessGraph.AppendEval(b, from, cp, EvalGames, EvalWeight, ChessVocabulary.EvalPgnSourceId, eventId);
            }

            string? q = Tok(qualityTokens, ply);
            if (q is not null)
                ChessGraph.AppendMoveQuality(b, from.Position.Id, q, 1, MoveWeight * 0.5, src, eventId);

            state = next;
            carried = to;
        }

        // Motifs are multi-ply facts (a sacrifice is only a sacrifice once the reply and
        // the settled exchange are known), so detection runs over the fully replayed
        // window after the walk. Same shapes at the same grains as the old per-ply pass:
        // line grain via GAME_HAS_MOTIF; position grain via HAS_MOTIF on the position
        // REACHED by the tagged ply — (position, HAS_MOTIF, concept), the shared-content
        // sibling of the line-grain cell (HAS_MOTIF's family root is GAME_HAS_MOTIF),
        // ctx = null so every game reaching the position corroborates one cell.
        var motifs = ChessMotifs.DetectGame(
            new ChessMotifs.ReplayWindow(boards, played, evals, standardStart));
        for (int ply = 0; ply < played.Count; ply++)
        {
            foreach (var tag in motifs[ply])
            {
                ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_MOTIF", tag, MoveWeight, src);
                ChessGraph.AppendPositionMotif(b, line[ply + 1].Id, tag, MoveWeight, src);
            }
        }

        // One linestring per LINE, deposited once the whole line is known. A game whose SAN
        // failed to resolve returned early above and deposits nothing — a partial line would
        // be a path the game never took. GH #736: the trajectory is a pure function of the
        // line, so it is deposited under the ChessTrajectory source (one lane = one source =
        // one evictable unit, #508) and stamped with the trajectory lane's own per-line
        // marker, so the standalone backfill skips lines this fused pass already carried.
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendGameTrajectory(b, lineId, line, ChessVocabulary.TrajectorySourceId, nowUs);
        b.AddEntity(ChessTrajectoryDecomposer.MarkerId(lineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, ChessVocabulary.TrajectorySourceId);
    }

    private static string? Tok(string?[]? arr, int i)
        => arr is not null && i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : null;
}
