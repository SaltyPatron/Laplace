using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessGraph
{
    // PlyOutcome is bit-identical to the attestation outcome enum on purpose, so
    // the score points are the Glicko2 constants — not a fourth transcription of
    // the same three literals. (Glicko2.ScoreDraw is itself pinned against the
    // native kScoreHalfFp definition by Glicko2Tests.)
    public static long ScoreFp1e9(PlyOutcome outcome) => outcome switch
    {
        PlyOutcome.Win => Glicko2.ScoreWin,
        PlyOutcome.Draw => Glicko2.ScoreDraw,
        _ => Glicko2.ScoreLoss,
    };

    // AGGREGATING lane only: deduped substructure/position outcome deposits + the MOVE edge.
    // Game-specific record edges (GAME_AT: subject unique per game; GAME_AT_PLY / PLAYED_BY:
    // one near-unique row per ply) were deliberately removed — they can never corroborate
    // across games, so each was a permanently single-witness consensus cell. The game's
    // verbatim HAS_MOVETEXT plus replay reconstructs all of them; contextId keeps per-game
    // provenance on the evidence rows.
    public static void AppendMoveEdge(
    SubstrateChangeBuilder b, string fromKey, string toKey, PlyOutcome outcome,
    long games, double witnessWeight,
    Hash128? sourceId = null, long moveChoiceGames = 0,
    Hash128? contextId = null)
    {
        var src = sourceId ?? ChessVocabulary.SourceId;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, src);
        var to = EmitNodes(b, toKey, nowUs, src);
        AppendMoveEdge(b, from, to, outcome, games, witnessWeight, src, moveChoiceGames, contextId);
    }

    // Already-staged overload: the per-ply analyzer loop stages each distinct position once
    // (ChessAnalyze) and hands the composed nodes to every Append* call for that ply.
    internal static void AppendMoveEdge(
    SubstrateChangeBuilder b, ChessComposed from, ChessComposed to, PlyOutcome outcome,
    long games, double witnessWeight,
    Hash128 sourceId, long moveChoiceGames = 0,
    Hash128? contextId = null)
    {
        if (games < 1) games = 1;
        if (moveChoiceGames < 1) moveChoiceGames = games;
        long sum = checked(ScoreFp1e9(outcome) * games);

        foreach (var s in from.Substructures)
            b.AddAttestation(Outcome(s.Id, games, sum, witnessWeight, sourceId, contextId));
        b.AddAttestation(Outcome(from.Position.Id, games, sum, witnessWeight, sourceId, contextId));

        long moveSum = checked(ScoreFp1e9(outcome) * moveChoiceGames);
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: from.Position.Id,
            typeId: ChessVocabulary.MoveType,
            obj: to.Position.Id,
            sourceId: sourceId,
            contextId: contextId,
            games: moveChoiceGames,
            sumScoreFp1e9: moveSum,
            witnessWeight: witnessWeight));
    }

    public static void AppendEval(
    SubstrateChangeBuilder b, string fromKey, int cpSideToMove, long games, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
        AppendEval(b, from, cpSideToMove, games, witnessWeight, sourceId, contextId);
    }

    internal static void AppendEval(
    SubstrateChangeBuilder b, ChessComposed from, int cpSideToMove, long games, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (games < 1) games = 1;
        long sum = PgnEvals.EvalSumFp1e9(cpSideToMove, games);
        foreach (var s in from.Substructures)
            b.AddAttestation(EvalRow(s.Id, games, sum, witnessWeight, sourceId, contextId));
        b.AddAttestation(EvalRow(from.Position.Id, games, sum, witnessWeight, sourceId, contextId));
    }

    public static void AppendMoveQuality(
    SubstrateChangeBuilder b, string fromKey, string qualityToken, long games, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, qualityToken, sourceId) is not { } qid) return;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
        b.AddAttestation(NativeAttestation.Categorical(
            from.Position.Id, "MOVE_QUALITY", qid, sourceId, contextId, witnessWeight,
            observationCount: games));
    }

    internal static void AppendMoveQuality(
    SubstrateChangeBuilder b, Hash128 positionId, string qualityToken, long games, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, qualityToken, sourceId) is not { } qid) return;
        b.AddAttestation(NativeAttestation.Categorical(
            positionId, "MOVE_QUALITY", qid, sourceId, contextId, witnessWeight,
            observationCount: games));
    }

    public static void AppendClock(
    SubstrateChangeBuilder b, string fromKey, string canonicalClock, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, canonicalClock, sourceId) is not { } cid) return;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
        b.AddAttestation(NativeAttestation.Categorical(
            from.Position.Id, "HAS_CLOCK", cid, sourceId, contextId, witnessWeight));
    }

    internal static void AppendClock(
    SubstrateChangeBuilder b, Hash128 positionId, string canonicalClock, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, canonicalClock, sourceId) is not { } cid) return;
        b.AddAttestation(NativeAttestation.Categorical(
            positionId, "HAS_CLOCK", cid, sourceId, contextId, witnessWeight));
    }

    public static void AppendEvalToken(
    SubstrateChangeBuilder b, string fromKey, string evalToken, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, evalToken, sourceId) is not { } tid) return;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
        b.AddAttestation(NativeAttestation.Categorical(
            from.Position.Id, "HAS_EVAL_TOKEN", tid, sourceId, contextId, witnessWeight));
    }

    internal static void AppendEvalToken(
    SubstrateChangeBuilder b, Hash128 positionId, string evalToken, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, evalToken, sourceId) is not { } tid) return;
        b.AddAttestation(NativeAttestation.Categorical(
            positionId, "HAS_EVAL_TOKEN", tid, sourceId, contextId, witnessWeight));
    }

    public static void AppendThinkClass(
    SubstrateChangeBuilder b, string fromKey, string thinkClass, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, thinkClass, sourceId) is not { } tid) return;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
        b.AddAttestation(NativeAttestation.Categorical(
            from.Position.Id, "HAS_THINK_CLASS", tid, sourceId, contextId, witnessWeight));
    }

    internal static void AppendThinkClass(
    SubstrateChangeBuilder b, Hash128 positionId, string thinkClass, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        if (ContentEmitter.Emit(b, thinkClass, sourceId) is not { } tid) return;
        b.AddAttestation(NativeAttestation.Categorical(
            positionId, "HAS_THINK_CLASS", tid, sourceId, contextId, witnessWeight));
    }

    public static void AppendGameMeta(
    SubstrateChangeBuilder b, Hash128 gameId, string relation, string canonicalValue,
    double witnessWeight, Hash128 sourceId)
    {
        if (ContentEmitter.Emit(b, canonicalValue, sourceId) is not { } vid) return;
        b.AddAttestation(NativeAttestation.Categorical(gameId, relation, vid, sourceId, null, witnessWeight));
    }

    /// <summary>
    /// Emit the position (and its substructures) as content nodes and return the position id.
    /// For lanes that attest onto a position without emitting a MOVE edge for it — e.g. the
    /// chess-book decomposer grounding prose commentary to the exact position it explains.
    /// </summary>
    public static Hash128 EmitPosition(SubstrateChangeBuilder b, string surface, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, surface, nowUs, src).Position.Id;
    }

    /// <summary>
    /// Compose + stage a position's nodes once and return the composed nodes, so a caller
    /// attesting several facts onto the same position per ply (ChessAnalyze) stages each
    /// distinct position a single time instead of re-staging it in every Append* helper.
    /// </summary>
    internal static ChessComposed EmitComposed(SubstrateChangeBuilder b, string surface, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, surface, nowUs, src);
    }

    private static ChessComposed EmitNodes(SubstrateChangeBuilder b, string surface, long nowUs, Hash128 src)
    {
        var c = ChessCompose.Position(surface);
        foreach (var s in c.Substructures) AddNode(b, s, ChessVocabulary.SubstructureType, nowUs, src);
        AddNode(b, c.Position, ChessVocabulary.PositionType, nowUs, src);
        return c;
    }

    private static void AddNode(SubstrateChangeBuilder b, in ChessNode n, Hash128 typeId, long nowUs, Hash128 src)
    {
        b.AddEntity(n.Id, n.Tier, typeId, src);
        b.AddPhysicality(new PhysicalityRow(
            Id: n.PhysId,
            EntityId: n.Id,
            SourceId: src,
            Type: PhysicalityType.Content,
            CoordX: n.Coord[0], CoordY: n.Coord[1], CoordZ: n.Coord[2], CoordM: n.Coord[3],
            HilbertIndex: n.Hb,
            TrajectoryXyzm: n.Trajectory,
            NConstituents: n.NConstituents,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: nowUs));
    }

    private static AttestationRow Outcome(
        Hash128 subject, long games, long sum, double witnessWeight, Hash128 src, Hash128? contextId = null) =>
        NativeAttestation.Aggregated(
            subject: subject,
            typeId: ChessVocabulary.OutcomeType,
            obj: ChessVocabulary.OutcomeObject,
            sourceId: src,
            contextId: contextId,
            games: games,
            sumScoreFp1e9: sum,
            witnessWeight: witnessWeight);

    private static AttestationRow EvalRow(
        Hash128 subject, long games, long sum, double witnessWeight, Hash128 src, Hash128? contextId = null) =>
        NativeAttestation.Aggregated(
            subject: subject,
            typeId: ChessVocabulary.HasEvalType,
            obj: ChessVocabulary.HasEvalObject,
            sourceId: src,
            contextId: contextId,
            games: games,
            sumScoreFp1e9: sum,
            witnessWeight: witnessWeight);
}
