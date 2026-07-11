using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessGraph
{
    public static long ScoreFp1e9(PlyOutcome outcome) => outcome switch
    {
        PlyOutcome.Win => 1_000_000_000L,
        PlyOutcome.Draw => 500_000_000L,
        _ => 0L,
    };

    public static void AppendMoveEdge(
    SubstrateChangeBuilder b, string fromKey, string toKey, PlyOutcome outcome,
    long games, double witnessWeight,
    Hash128? sourceId = null, Hash128? moverPlayerId = null, long moveChoiceGames = 0,
    Hash128? contextId = null, int ply = -1)
    {
        var src = sourceId ?? ChessVocabulary.SourceId;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        if (games < 1) games = 1;
        if (moveChoiceGames < 1) moveChoiceGames = games;
        long sum = checked(ScoreFp1e9(outcome) * games);

        var from = EmitNodes(b, fromKey, nowUs, src);
        var to = EmitNodes(b, toKey, nowUs, src);

        foreach (var s in from.Substructures)
            b.AddAttestation(Outcome(s.Id, games, sum, witnessWeight, src, contextId));
        b.AddAttestation(Outcome(from.Position.Id, games, sum, witnessWeight, src, contextId));

        long moveSum = checked(ScoreFp1e9(outcome) * moveChoiceGames);
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: from.Position.Id,
            typeId: ChessVocabulary.MoveType,
            obj: to.Position.Id,
            sourceId: src,
            contextId: contextId,
            games: moveChoiceGames,
            sumScoreFp1e9: moveSum,
            witnessWeight: witnessWeight));

        if (moverPlayerId is { } mover)
            b.AddAttestation(NativeAttestation.Categorical(
                from.Position.Id, "PLAYED_BY", mover, src, contextId, witnessWeight));

        if (contextId is { } gameId)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                gameId, "GAME_AT", from.Position.Id, src, contextId: null, witnessWeight));
            if (ply >= 0 && ContentEmitter.Emit(b, ply.ToString(), src) is { } plyId)
                b.AddAttestation(NativeAttestation.Categorical(
                    from.Position.Id, "GAME_AT_PLY", plyId, src, gameId, witnessWeight));
        }
    }

    public static void AppendEval(
    SubstrateChangeBuilder b, string fromKey, int cpSideToMove, long games, double witnessWeight,
    Hash128 sourceId, Hash128? contextId = null)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        if (games < 1) games = 1;
        long sum = PgnEvals.EvalSumFp1e9(cpSideToMove, games);
        var from = EmitNodes(b, fromKey, nowUs, sourceId);
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
