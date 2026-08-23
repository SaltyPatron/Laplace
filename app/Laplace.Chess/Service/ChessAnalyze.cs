using System.Linq;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// CALCULATED layer. Derives positions / geometry / motifs / opening classification /
// consensus by REPLAYING a game's witnessed movetext. Pure deterministic function of the witnessed
// inputs (movetext + start FEN + per-ply annotation tokens the recorder stored). Emitted under the
// analysis source and stamped ANALYZED_AT=Version so the analyzer scan skips already-derived games.
//
// Scan driver: <see cref="ChessWitnessHydrator"/> reads witnessed attestations from Postgres.
// PGN path is legacy bootstrap only when an explicit file path is passed.
public static class ChessAnalyze
{
    public const int Version = 3;
    public static Hash128 SourceId => ChessVocabulary.AnalysisSourceId;

    private const double MoveWeight = 0.7;
    private const double MetaWeight = 0.7;
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
        // Metadata on the trunk, not rated testimony -- see ChessVocabulary
        // .AnalysisVersionMetaTypeId. A substrate meta-type is not in relation_types.toml
        // and therefore never folds, so this stops minting one unrateable consensus cell
        // per analysed game.
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddEntity(ChessVocabulary.AnalysisVersionMetaTypeId, EntityTier.Word,
                    BootstrapIntentBuilder.RelationTypeMetaTypeId, SourceId)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    eventId, ChessVocabulary.AnalysisVersionMetaTypeId, vId,
                    SourceId, contextId: null, ChessVocabulary.Trust));
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

        // Replay builds the ONE ordered line physicality. Its vertices are deterministic move
        // points (the positions before/after each move), resolved from the chess perfcache or
        // the identical compose fallback. They are not independently deposited SQL position
        // entities/physicalities merely because this game passed through them.
        var state = initial;
        var line = new List<ChessNode>(sans.Count + 1);
        var boards = new List<Board>(sans.Count + 1) { initial.Board };
        var played = new List<ChessMove>(sans.Count);
        var scratch = new List<ChessMove>(16);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            var mv = San.Resolve(state.Board, sans[ply], scratch);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            var from = ChessGraph.ComposePositionPoint(state.Board);
            var next = m.Apply(state, mv.Value);
            var to = ChessGraph.ComposePositionPoint(next.Board);
            if (line.Count == 0) line.Add(from);
            line.Add(to);
            boards.Add(next.Board);
            played.Add(mv.Value);

            string? clk = Tok(clockTokens, ply);
            if (clk is not null)
            {
                if (clocks.Length > 0)
                {
                    double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
                    ChessGraph.AppendThinkOutcome(
                        b, ChessCanonical.ThinkClass(tf), result.ForMover(mover), MetaWeight, src);
                    if (ChessCanonical.ThinkLens(ply, sans.Count, tf, clocks[ply],
                            (ply & 1) == 0 ? medianRemEven : medianRemOdd, medianDrop) is { } lens)
                        ChessGraph.AppendThinkOutcome(
                            b, lens, result.ForMover(mover), MetaWeight, src);
                }
            }
            else if (spentSeconds is not null && medianSpent > 0)
            {
                double tf = PgnClocks.ThinkFactorFromSpent(spentSeconds, medianSpent, ply);
                ChessGraph.AppendThinkOutcome(
                    b, ChessCanonical.ThinkClass(tf), result.ForMover(mover), MetaWeight, src);
                if (ChessCanonical.ThinkLens(ply, sans.Count, tf,
                        remaining: 0, medianRemaining: 0, medianDrop: 0) is { } lens)
                    ChessGraph.AppendThinkOutcome(
                        b, lens, result.ForMover(mover), MetaWeight, src);
            }

            state = next;
        }

        // A motif is a property of the played line/window. Do not manufacture an exact-board
        // occurrence edge in addition to the line classification.
        var motifs = ChessMotifs.DetectGame(
            new ChessMotifs.ReplayWindow(boards, played, evals, standardStart));
        for (int ply = 0; ply < played.Count; ply++)
        {
            foreach (var tag in motifs[ply])
                ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_MOTIF", tag, MoveWeight, src);
        }

        // One linestring per LINE, deposited once the whole line is known. A game whose SAN
        // failed to resolve returned early above and deposits nothing — a partial line would
        // be a path the game never took. GH #736: the trajectory is a pure function of the
        // line, so it is deposited under the ChessTrajectory source (one lane = one source =
        // one evictable unit, #508) and stamped with the trajectory lane's own per-line
        // marker, so the standalone backfill skips lines this fused pass already carried.
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendPositionProjection(b, lineId, line, ChessVocabulary.TrajectorySourceId, nowUs);
        b.AddEntity(ChessTrajectoryDecomposer.MarkerId(lineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, ChessVocabulary.TrajectorySourceId);
    }

    private static string? Tok(string?[]? arr, int i)
        => arr is not null && i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : null;
}
