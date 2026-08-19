using System.Linq;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// CALCULATED layer. Derives positions / geometry / bounded outcome projections / motifs / opening classification /
// consensus by REPLAYING a game's witnessed movetext. Pure deterministic function of the witnessed
// inputs (movetext + start FEN + per-ply annotation tokens the recorder stored). Emitted under the
// analysis source and stamped ANALYZED_AT=Version so the analyzer scan skips already-derived games.
//
// Scan driver: <see cref="ChessWitnessHydrator"/> reads witnessed attestations from Postgres.
// PGN path is legacy bootstrap only when an explicit file path is passed.
public static class ChessAnalyze
{
    public const int Version = 2;
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
        var (lineId, playingId, moves, result, wp, bp, startFen, clockTokens, evalTokens, qualityTokens, spentSeconds) = witnessed;

        var clocks = clockTokens is not null
            ? clockTokens.Select(t => t is null ? 0.0 : ParseClockSeconds(t)).ToArray()
            : System.Array.Empty<double>();
        double medianDrop = PgnClocks.MedianDrop(clocks);
        var evals = evalTokens is not null
            ? evalTokens.Select(t => t is null ? 0 : PgnEvals.ParseToken(t)).ToArray()
            : null;

        DeriveGame(b, lineId, playingId, result, moves, startFen, wp, bp,
                   clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth,
                   spentSeconds);

        // Analyzer unit = PLAYING (not tournament Chess_Event). Marker per playing.
        b.AddEntity(ChessVocabulary.AnalysisMarkerId(playingId, Version), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, SourceId);
    }

    internal static ChessWitnessedGame WitnessedFromParsed(ChessGameRecord parsed)
    {
        var (gameText, moves, result, lineId, _, playingId) = parsed;
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
            lineId, playingId, moves, result, wp, bp, startFen, clockTokens, evalTokens, qualityTokens,
            spentSeconds);
    }

    private static bool ValidName(string n) => !string.IsNullOrWhiteSpace(n) && n != "?";
    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static double ParseClockSeconds(string t)
        => PgnClocks.TryParseHms(t, out double sec) ? sec : 0;

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
        // Unreadable start = derive nothing. The recorder already refused this game, and
        // deriving from a substituted board is how a game we could not read became a game we
        // invented.
        if (InitialState(startFen, m) is not { } start) return;
        var (initial, standardStart) = start;

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

    /// <summary>
    /// The game's starting board, or NULL when the PGN asserted a start position this parser
    /// cannot model.
    ///
    /// This used to swallow the FormatException and return m.Initial() — the STANDARD start —
    /// with StandardStart=true. A Chess960 game (chess.com exports X-FEN castling for every
    /// one) was therefore replayed from rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR, and
    /// whatever SAN happened to resolve against that wrong board was recorded, folded into
    /// consensus, and reported under a run whose status said ok and failed=0. A game we could
    /// not read was not dropped; it was invented.
    ///
    /// Null instead. The caller refuses the game and counts it. An unreadable record is not a
    /// standard record, and it is not this layer's business to guess which board was meant.
    /// </summary>
    public static (ChessState Initial, bool StandardStart)? InitialState(string? startFen, ChessModality m)
    {
        if (string.IsNullOrWhiteSpace(startFen)) return (m.Initial(), true);
        try { return (m.FromFen(startFen), false); }
        catch (FormatException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"ChessAnalyze: unreadable start position, game refused: {ex.Message}");
            return null;
        }
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
        // Think-lens thresholds, all from the game's OWN clock story (no constants):
        // per-side median remaining (parity = ply mod 2 — same-parity plies are one
        // player's regardless of who moved first) is the low-clock line; medianDrop is
        // the flagging line. Both 0 for the spent dialect (no remaining clock witnessed).
        double medianRemEven = PgnClocks.MedianRemaining(clocks, 0);
        double medianRemOdd = PgnClocks.MedianRemaining(clocks, 1);
        bool mate = sans.Count > 0 && sans[^1].IndexOf('#') >= 0;
        int? winner = result.IsDraw ? null : result.Winner;
        // Reused across plies so the TT warms; only built when engine-eval is requested.
        var engine = engineDepth > 0 ? new Search(EvalTerm.All) : null;

        // Directed SAN converse.resolve(same as LineId replay) — full Legal() per ply was ~46% of
        // analyze time. Apply still owns repetition history for the motif window boards.
        var state = initial;
        ChessComposed? carried = null;
        var line = new List<ChessNode>(sans.Count + 1);
        var boards = new List<Board>(sans.Count + 1) { initial.Board };
        var played = new List<ChessMove>(sans.Count);
        var scratch = new List<ChessMove>(16);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            var mv = San.Resolve(state.Board, sans[ply], scratch);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            // Our OWN eval (high-trust ChessAnalysis witness) competes on (position, HAS_EVAL) with
            // the PGN's eval (lower-trust EvalPgn, emitted below). Score is side-to-move cp.
            var from = carried ?? ChessGraph.EmitComposed(b, m.StateKey(state), src);
            if (engine is not null)
            {
                int ourCp = engine.Think(state.Board, new Search.Limits(MaxDepth: engineDepth)).Score;
                ChessGraph.AppendEval(b, from, ourCp, games: 1, witnessWeight: 0.9, src, eventId);
            }
            var next = m.Apply(state, mv.Value);
            var to = ChessGraph.EmitComposed(b, m.StateKey(next), src);
            if (line.Count == 0) line.Add(from.Position);
            line.Add(to.Position);
            boards.Add(next.Board);
            played.Add(mv.Value);

            long games = 1;
            if (mate && winner == mover) games += 1;

            ChessGraph.AppendSubstructureOutcome(
                b, from, result.ForMover(mover), games, MoveWeight, src);


            string? clk = Tok(clockTokens, ply);
            if (clk is not null)
            {
                ChessGraph.AppendClock(b, from.Position.Id, clk, MetaWeight, src, eventId);
                if (clocks.Length > 0)
                {
                    double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
                    ChessGraph.AppendThinkClass(b, from.Position.Id, ChessCanonical.ThinkClass(tf), MetaWeight, src, eventId);
                    // Phase × clock × spent lens beside the base class. Lens strings are
                    // content values on the same HAS_THINK_CLASS cell shape — no manifest
                    // change, and no ChessAnalyze.Version bump: re-deriving old games at
                    // the new vocabulary rides `laplace evict` (the PR-4 eviction lane).
                    if (ChessCanonical.ThinkLens(ply, sans.Count, tf, clocks[ply],
                            (ply & 1) == 0 ? medianRemEven : medianRemOdd, medianDrop) is { } lens)
                        ChessGraph.AppendThinkClass(b, from.Position.Id, lens, MetaWeight, src, eventId);
                }
            }
            else if (spentSeconds is not null && medianSpent > 0)
            {
                // cutechess dialect (GH #494): per-move spent time is the think signal directly.
                // No HAS_CLOCK deposit — the source never asserted a remaining clock.
                double tf = PgnClocks.ThinkFactorFromSpent(spentSeconds, medianSpent, ply);
                ChessGraph.AppendThinkClass(b, from.Position.Id, ChessCanonical.ThinkClass(tf), MetaWeight, src, eventId);
                // Spent dialect: no remaining clock witnessed, so only the phase × spent
                // lens can derive (clock lenses would fabricate a quantity the source
                // never asserted — the same law that forbids synthetic HAS_CLOCK above).
                if (ChessCanonical.ThinkLens(ply, sans.Count, tf,
                        remaining: 0, medianRemaining: 0, medianDrop: 0) is { } lens)
                    ChessGraph.AppendThinkClass(b, from.Position.Id, lens, MetaWeight, src, eventId);
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
