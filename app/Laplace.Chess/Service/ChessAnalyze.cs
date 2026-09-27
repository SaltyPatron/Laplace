using System.Linq;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// Versioned calculation over a recorded playing: replays its witnessed movetext (plus start FEN
// and per-ply clock/eval/quality tokens) and derives opening classification, think-time and
// tactic outcomes, motifs and the line trajectory. Deterministic in those inputs. Testimony is
// attested under the analysis source, a calculation witness distinct from the recording source,
// and each playing is stamped with its analysis-version marker and unit completion. Inputs come
// either from a just-parsed game or from ChessWitnessHydrator reading the recorded attestations.
public static class ChessAnalyze
{
    public const int Version = 3;
    public static Hash128 SourceId => ChessVocabulary.AnalysisSourceId;

    private const double MoveWeight = 0.7;
    private const double MetaWeight = 0.7;
    // Assembles DeriveGame's inputs from a parsed game, derives, and stamps the
    // (playing, version) marker the analysis scan probes.
    internal static bool DeriveFromParsed(SubstrateChangeBuilder b, ChessGameRecord parsed)
        => DeriveFromParsed(b, parsed, ChessPgnDecomposer.MaterializeParsedReplay(parsed));

    internal static bool DeriveFromParsed(
        SubstrateChangeBuilder b, ChessGameRecord parsed, ChessParsedReplay replay)
        => DeriveFromWitnessed(b, WitnessedFromParsed(parsed), replay);

    /// <summary>Derive from substrate-hydrated witnessed inputs (no PGN re-parse).</summary>
    internal static bool DeriveFromWitnessed(SubstrateChangeBuilder b, ChessWitnessedGame witnessed, int engineDepth = 0)
        => DeriveFromWitnessed(b, witnessed, replay: null, engineDepth);

    private static bool DeriveFromWitnessed(
        SubstrateChangeBuilder b, ChessWitnessedGame witnessed,
        ChessParsedReplay? replay, int engineDepth = 0)
    {
        b.DeclareSourcePrior(SourceId, SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(ChessTacticOutcomes.SourceId, SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(ChessVocabulary.TrajectorySourceId, SourceTrust.StructuredCorpus);
        var (lineId, playingId, moves, result, wp, bp, startFen, clockTokens, evalTokens, qualityTokens, spentSeconds) = witnessed;

        var clocks = clockTokens is not null
            ? clockTokens.Select(t => t is null ? 0.0 : ParseClockSeconds(t)).ToArray()
            : System.Array.Empty<double>();
        double medianDrop = PgnClocks.MedianDrop(clocks);
        var evals = evalTokens is not null
            ? evalTokens.Select(t => t is null ? 0 : PgnEvals.ParseToken(t)).ToArray()
            : null;

        if (!DeriveGame(b, lineId, playingId, result, moves, startFen, wp, bp,
                       clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth,
                       spentSeconds, replay))
            return false;

        // The analysis unit is the playing; one marker and completion per playing and version.
        var marker = ChessVocabulary.AnalysisMarkerId(playingId, Version);
        b.AddEntity(marker, EntityTier.Document, ChessVocabulary.AnalysisMarkerType);
        IngestUnitCompletion.Emit(b, marker, SourceId, 21);
        return true;
    }

    internal static ChessWitnessedGame WitnessedFromParsed(ChessGameRecord parsed)
    {
        var (gameText, moves, result, lineId, _, playingId) = parsed;
        var walk = parsed.Walk;
        string whiteName = parsed.WhiteName ?? PgnGames.TagStr(gameText, "White");
        string blackName = parsed.BlackName ?? PgnGames.TagStr(gameText, "Black");
        Hash128? wp = ValidName(whiteName) ? ChessVocabulary.PlayerId(whiteName) : null;
        Hash128? bp = ValidName(blackName) ? ChessVocabulary.PlayerId(blackName) : null;
        string? startFen = parsed.StartFen;

        int mc = moves.Count;
        var clockTokens = PgnClocks.ClockTokens(gameText, mc);
        // cutechess dialect: no remaining-clock tokens, but per-move spent time.
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
    // POV pre-sign). Line-grain facts (opening, motif) take the LINE as subject; per-playing
    // testimony carries the playing as context.
    internal static bool DeriveGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, GameOutcome result,
        IReadOnlyList<string> sans, string? startFen,
        Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth = 0, double[]? spentSeconds = null,
        ChessParsedReplay? replay = null)
    {
        var m = new ChessModality();
        // An unreadable start position derives nothing; no substitute board is assumed.
        if (InitialState(startFen, m) is not { } start) return false;
        var (initial, standardStart) = start;

        if (!AppendGame(b, m, initial, sans, result, whitePlayer, blackPlayer, lineId, eventId,
                       clocks, medianDrop, clockTokens, evalTokens, evals, qualityTokens, engineDepth,
                       spentSeconds, standardStart, replay))
            return false;

        // Records the analysis version on the playing under a meta-type
        // (ChessVocabulary.AnalysisVersionMetaTypeId). A meta-type is not a governed relation,
        // so this is provenance on the trunk and never folds into a consensus cell.
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddEntity(ChessVocabulary.AnalysisVersionMetaTypeId, EntityTier.Word,
                    BootstrapIntentBuilder.RelationTypeMetaTypeId)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    eventId, ChessVocabulary.AnalysisVersionMetaTypeId, vId,
                    SourceId, contextId: null, ChessVocabulary.Trust));
        else
            throw new InvalidDataException("analysis version could not be admitted as content");
        return true;
    }

    /// <summary>
    /// The game's starting board, or null when the PGN asserts a start position this parser
    /// cannot model (for example X-FEN castling). The caller refuses and counts the game; the
    /// standard start is used only when no start FEN is given.
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

    // Line-grain classification: the subject is the line, and each playing of it is one more
    // witness to the same categorical consensus cell.
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

    private static bool AppendGame(
        SubstrateChangeBuilder b, ChessModality m, ChessState initial, IReadOnlyList<string> sans,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, Hash128 lineId, Hash128 eventId,
        double[] clocks, double medianDrop,
        string?[]? clockTokens, string?[]? evalTokens, int[]? evals, string?[]? qualityTokens,
        int engineDepth, double[]? spentSeconds = null, bool standardStart = true,
        ChessParsedReplay? replay = null)
    {
        var src = SourceId;
        double medianSpent = PgnClocks.MedianSpent(spentSeconds);
        // Think-lens thresholds come from this game's own clocks: per-side median remaining
        // (parity = ply mod 2; same-parity plies are one player's regardless of who moved
        // first) is the low-clock line; medianDrop is the flagging line. Both are 0 for the
        // spent-time dialect, which has no remaining clock.
        double medianRemEven = PgnClocks.MedianRemaining(clocks, 0);
        double medianRemOdd = PgnClocks.MedianRemaining(clocks, 1);

        // Replay builds the one ordered line trajectory. Its vertices are the composed
        // position points before/after each move (from the parsed replay or composed here);
        // passing through a position does not deposit that position's own rows.
        bool useReplay = replay is not null
            && replay.Boards.Length == sans.Count + 1
            && replay.Positions.Length == replay.Boards.Length
            && replay.Moves.Length == sans.Count;
        var state = initial;
        var line = new List<ChessNode>(sans.Count + 1);
        if (sans.Count == 0)
            line.Add(ChessGraph.ComposePositionPoint(initial.Board));
        var boards = useReplay
            ? new List<Board>(replay!.Boards)
            : new List<Board>(sans.Count + 1) { initial.Board };
        var played = new List<ChessMove>(sans.Count);
        var scratch = new List<ChessMove>(16);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            ChessMove mv;
            ChessNode from;
            ChessNode to;
            if (useReplay)
            {
                // The parser already resolved and validated this move against this board;
                // its move and positions are reused instead of resolving SAN again.
                mv = replay!.Moves[ply];
                from = replay.Positions[ply].Position;
                to = replay.Positions[ply + 1].Position;
            }
            else
            {
                var resolved = San.Resolve(state.Board, sans[ply], scratch);
                if (resolved is null) return false;
                mv = resolved.Value;
                from = ChessGraph.ComposePositionPoint(state.Board);
                var next = m.Apply(state, mv);
                to = ChessGraph.ComposePositionPoint(next.Board);
                state = next;
                boards.Add(next.Board);
            }
            if (line.Count == 0) line.Add(from);
            line.Add(to);
            played.Add(mv);
        }

        // Nothing is emitted until the whole replay is legal. Emission order: opening
        // labels, per-ply think outcomes, motifs, tactic outcomes, then the trajectory.
        if (standardStart) ClassifyOpening(b, lineId, sans, m);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            int mover = boards[ply].WhiteToMove ? 0 : 1;
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
        }

        // Motifs are attested on the line as labels; the tactical patterns are also attested
        // against the game's result (ChessTacticOutcomes), which is the pattern → outcome
        // standing search reads.
        var motifs = ChessMotifs.DetectGame(
            new ChessMotifs.ReplayWindow(boards, played, evals, standardStart));
        for (int ply = 0; ply < played.Count; ply++)
        {
            foreach (var tag in motifs[ply])
                ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_MOTIF", tag, MoveWeight, src);
        }
        ChessTacticOutcomes.AppendGame(
            b, boards, result, eventId,
            ChessTacticOutcomes.SourceId, witnessWeight: 0.9);

        // One trajectory per line, deposited only once the whole line resolved (an unresolved
        // SAN returned above, so no partial path is written). The trajectory is a function of
        // the line alone, so it is attributed to the trajectory source and stamped with the
        // per-line trajectory marker that ChessTrajectoryDecomposer's scan skips.
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendPositionProjection(b, lineId, line, ChessVocabulary.TrajectorySourceId, nowUs);
        b.AddEntity(ChessTrajectoryDecomposer.MarkerId(lineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType);
        IngestUnitCompletion.Emit(
            b, ChessTrajectoryDecomposer.MarkerId(lineId), ChessVocabulary.TrajectorySourceId, 21);
        return true;
    }

    private static string? Tok(string?[]? arr, int i)
        => arr is not null && i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : null;
}
