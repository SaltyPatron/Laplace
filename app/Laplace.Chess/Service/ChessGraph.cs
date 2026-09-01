using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessGraph
{
    /// <summary>
    /// Compose one move point without depositing a SQL position subtree. A recorded game owns
    /// one line physicality; the deterministic position addresses and coordinates inside it
    /// come from the chess perfcache or the identical compose fallback. A source that actually
    /// asserts a fact about an exact position uses <see cref="EmitPosition"/> instead.
    /// </summary>
    internal static ChessNode ComposePositionPoint(string surface)
        => ChessCompose.Position(surface).Position;

    internal static ChessNode ComposePositionPoint(Board board)
        => ChessCompose.Position(board).Position;

    /// <summary>
    /// Compose one move point without depositing its substructure atoms. Peer of
    /// <see cref="ComposePositionPoint"/>: a CATALOG line needs the move ids for the line
    /// merkle and its trajectory, but asserts nothing about the moves themselves, so it must
    /// not stage the reusable move objects. A source that actually witnesses a move played
    /// uses <see cref="EmitMove"/> instead.
    /// </summary>
    internal static ChessNode ComposeMovePoint(Piece moving, ChessMove move)
        => ChessCompose.Move(moving, move).Move;

    /// <summary>
    /// Stage one bounded reusable move object. A move is piece×from×to×special×promotion;
    /// it is neither a board nor testimony. Which game played it is carried by the playing
    /// trajectory, and its state transition is the separate deterministic transition floor.
    /// </summary>
    internal static ChessNode EmitMove(
        SubstrateChangeBuilder b, Piece moving, ChessMove move, Hash128 src, long nowUs)
    {
        var composed = ChessCompose.Move(moving, move);
        if (b.PresenceOracle?.IsProvenPresent(composed.Move.Id) == true) return composed.Move;
        foreach (var field in composed.Fields)
            AddNode(b, field, ChessVocabulary.SubstructureType, nowUs, src);
        AddNode(b, composed.Move, ChessVocabulary.MoveType, nowUs, src);
        return composed.Move;
    }

    internal static ChessNode EmitAnnotationMissing(
        SubstrateChangeBuilder b, Hash128 src, long nowUs)
    {
        var node = ChessCompose.AnnotationMissing();
        AddNode(b, node, ChessVocabulary.SubstructureType, nowUs, src);
        return node;
    }

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
        // The verdict is about this evaluated position. Projecting it onto every
        // constituent makes one engine observation look like 25-36 independent
        // observations and precomputes every possible structural question.
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

    /// <summary>
    /// Fold the reusable time-behaviour class without projecting its source occurrence onto an
    /// exact position. The occurrence remains losslessly present in the playing trajectory.
    /// </summary>
    internal static void AppendThinkOutcome(
        SubstrateChangeBuilder b, string thinkClass, PlyOutcome moverOutcome,
        double witnessWeight, Hash128 sourceId)
    {
        if (ContentEmitter.Emit(b, thinkClass, sourceId) is not { } tid) return;
        b.AddAttestation(Outcome(
            tid, games: 1, ScoreFp1e9(moverOutcome), witnessWeight, sourceId,
            contextId: null));
    }

    public static void AppendGameMeta(
    SubstrateChangeBuilder b, Hash128 gameId, string relation, string canonicalValue,
    double witnessWeight, Hash128 sourceId)
    {
        if (ContentEmitter.Emit(b, canonicalValue, sourceId) is not { } vid) return;
        b.AddAttestation(NativeAttestation.Categorical(gameId, relation, vid, sourceId, null, witnessWeight));
    }

    /// <summary>
    /// Materialize the deterministic state transition witnessed by a playing. The bounded
    /// consensus cell is position --MOVE--> position; the move object remains the reusable
    /// piece/from/to/special composition and (from, move) resolves to the same destination in
    /// <see cref="ChessTransitionFloor"/>. Context is the playing, so replaying the same source
    /// occurrence deduplicates while independent games add witnesses to one transition cell.
    /// </summary>
    internal static void AppendTransitions(
        SubstrateChangeBuilder b, IReadOnlyList<Hash128> positions, GameOutcome result,
        double witnessWeight, Hash128 sourceId, Hash128 playingId)
    {
        if (positions.Count < 2) return;
        for (int ply = 0; ply + 1 < positions.Count; ply++)
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: positions[ply],
                typeId: ChessVocabulary.MoveType,
                obj: positions[ply + 1],
                sourceId: sourceId,
                contextId: playingId,
                games: 1,
                sumScoreFp1e9: ScoreFp1e9(result.ForMover(ply & 1)),
                witnessWeight: witnessWeight));
    }

    /// <summary>
    /// Emit the position (and its substructures) as content nodes and return the position id.
    /// For lanes that attest onto a position — e.g. the chess-book decomposer grounding prose
    /// commentary to the exact position it explains.
    /// </summary>
    public static Hash128 EmitPosition(SubstrateChangeBuilder b, string surface, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, surface, nowUs, src).Position.Id;
    }

    public static Hash128 EmitPosition(SubstrateChangeBuilder b, Board board, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, board, nowUs, src).Position.Id;
    }

    /// <summary>
    /// Compose + stage a position's nodes once for a source that actually asserts facts about
    /// that exact board (for example Syzygy or an evictable engine-analysis product).
    /// </summary>
    internal static ChessComposed EmitComposed(SubstrateChangeBuilder b, string surface, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, surface, nowUs, src);
    }

    internal static ChessComposed EmitComposed(SubstrateChangeBuilder b, Board board, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        return EmitNodes(b, board, nowUs, src);
    }

    /// <summary>
    /// Stage an already-composed position without repeating ChessCompose.Position. This is the
    /// fused PGN path: one N+1 replay materialization can feed analysis, position-outcome facts,
    /// and the line projection while each lane still deposits only the state it owns.
    /// </summary>
    internal static ChessComposed EmitComposed(
        SubstrateChangeBuilder b, ChessComposed composed, Hash128 src)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        StageNodes(b, composed, nowUs, src);
        return composed;
    }

    private static ChessComposed EmitNodes(SubstrateChangeBuilder b, string surface, long nowUs, Hash128 src)
    {
        var c = ChessCompose.Position(surface);
        StageNodes(b, c, nowUs, src);
        return c;
    }

    private static ChessComposed EmitNodes(SubstrateChangeBuilder b, Board board, long nowUs, Hash128 src)
    {
        var c = ChessCompose.Position(board);
        StageNodes(b, c, nowUs, src);
        return c;
    }

    private static void StageNodes(
        SubstrateChangeBuilder b, ChessComposed c, long nowUs, Hash128 src)
    {
        // TRUNK SHORT-CIRCUIT. A position whose id is already proven deposited implies its whole
        // substructure subtree is too — they were staged together the first time, and the id is a
        // Merkle over exactly those constituents, so the trunk cannot exist without them. Staging
        // them again produces byte-identical rows that apply then dedups away: pure cost.
        //
        // This is the law the shared content path has always followed (ContentTierSpine.cs:127-133
        // asks ContentLadderLedger.IsPersisted before staging anything) and that the chess lane
        // never joined. MEASURED: the compose probe stages 227 entities per game where the live
        // run keeps ~65, and row building is 56.2% of record+analyze.
        //
        // The oracle answers false for anything not yet probed, so this can only ever skip work
        // that was provably redundant — never something absent. Ids are still COMPOSED (callers
        // need them for the attestations below); only the staging is skipped, so the fold is
        // untouched.
        if (b.PresenceOracle?.IsProvenPresent(c.Position.Id) == true) return;

        foreach (var s in c.Substructures) AddNode(b, s, ChessVocabulary.SubstructureType, nowUs, src);
        AddNode(b, c.Position, ChessVocabulary.PositionType, nowUs, src);
    }

    /// <summary>
    /// Calculated position projection: one GeometryZM linestring on the line entity whose
    /// vertices are the positions it passed through, in order, with the position ids bit-packed
    /// into the mantissa channel. Chess-trajectory parity with the model lane — a game deposits
    /// its whole move sequence as ONE geometric object, exactly as a circuit deposits its whole
    /// relational assertion as one testimony-packed linestring.
    ///
    /// This is what the ply sequence was missing. Positions were already resident and geometric,
    /// and every game's line could be reconstructed by replaying its move trajectory — but the line
    /// itself was not an object, so "games near this line", maneuver search and transposition
    /// detection had nothing to index. With it they are GiST-backed spatial queries.
    ///
    /// Calculated layer, deliberately: this is deposited under the analyzer's source, versioned
    /// and evictable, never confused with the witnessed playing trajectory. And it
    /// is a PHYSICALITY, never part of the game's id — geometry is identity and reconstruction,
    /// not semantics, and coord/hilbert equality is not identity above tier 0.
    /// </summary>
    public static void AppendPositionProjection(
        SubstrateChangeBuilder b, Hash128 gameId, IReadOnlyList<ChessNode> line, Hash128 src, long nowUs)
        => AppendOrderedTrajectory(b, gameId, line, src, nowUs, PhysicalityType.Projection);

    /// <summary>
    /// The irreducible reusable line: ordered typed move objects. Individual playings point to
    /// this content; board positions are deterministic transition projections.
    /// </summary>
    internal static void AppendLineTrajectory(
        SubstrateChangeBuilder b, Hash128 lineId, IReadOnlyList<ChessNode> moves,
        Hash128 src, long nowUs)
        => AppendOrderedTrajectory(b, lineId, moves, src, nowUs, PhysicalityType.Content);

    /// <summary>
    /// One compact parallel sequence for occurrence annotations. Ordinals align exactly with
    /// the playing's move trajectory; missing values use one typed sentinel. This preserves
    /// source structure without one entity/attestation/consensus row per ply.
    /// </summary>
    internal static void AppendPlayingAnnotationTrajectory(
        SubstrateChangeBuilder b, Hash128 playingId, IReadOnlyList<Hash128> values,
        IReadOnlyList<ChessNode> movePoints, PhysicalityType type, Hash128 src, long nowUs)
    {
        if (values.Count == 0 || values.Count != movePoints.Count) return;
        var coords = new double[(long)movePoints.Count * 4];
        for (int i = 0; i < movePoints.Count; i++)
            movePoints[i].Coord.CopyTo(coords, i * 4);
        double[] centroid = Math4d.KarcherMean(coords);
        b.AddPhysicality(new PhysicalityRow(
            Id: PhysicalityId.Compute(playingId, type), EntityId: playingId, SourceId: src,
            Type: type,
            CoordX: centroid[0], CoordY: centroid[1], CoordZ: centroid[2], CoordM: centroid[3],
            HilbertIndex: Hilbert128.Encode(centroid), TrajectoryXyzm: Trajectory.Build([.. values]),
            NConstituents: values.Count, AlignmentResidual: null, SourceDim: null,
            ObservedAtUnixUs: nowUs));
    }

    private static void AppendOrderedTrajectory(
        SubstrateChangeBuilder b, Hash128 entityId, IReadOnlyList<ChessNode> points,
        Hash128 src, long nowUs, PhysicalityType type = PhysicalityType.Content)
    {
        if (points.Count == 0) return;

        var ids = new Hash128[points.Count];
        var coords = new double[(long)points.Count * 4];
        for (int i = 0; i < points.Count; i++)
        {
            ids[i] = points[i].Id;
            coords[i * 4 + 0] = points[i].Coord[0];
            coords[i * 4 + 1] = points[i].Coord[1];
            coords[i * 4 + 2] = points[i].Coord[2];
            coords[i * 4 + 3] = points[i].Coord[3];
        }

        // Same primitives the position tier composes with — one implementation of "pack an
        // ordered id sequence into a trajectory", not a chess-specific second one.
        double[] traj = Trajectory.Build(ids);
        // Karcher, not Centroid — intrinsic mean, lands on S3 at norm 1. See
        // NgramTrajectory for the measurement. Requires a reseed.
        double[] centroid = Math4d.KarcherMean(coords);

        b.AddPhysicality(new PhysicalityRow(
            Id: PhysicalityId.Compute(entityId, type),
            EntityId: entityId,
            SourceId: src,
            Type: type,
            CoordX: centroid[0], CoordY: centroid[1], CoordZ: centroid[2], CoordM: centroid[3],
            HilbertIndex: Hilbert128.Encode(centroid),
            TrajectoryXyzm: traj,
            NConstituents: points.Count,
            AlignmentResidual: null,
            SourceDim: null,
            ObservedAtUnixUs: nowUs));
    }

    // Staged once per DISTINCT node per batch, not once per ply. A position's ~34 substructure
    // tokens recur in nearly every position of the same game, so this is called ~2,380 times per
    // game to keep ~222 physicalities — claim the id first and construct the row only on a miss.
    private static void AddNode(SubstrateChangeBuilder b, in ChessNode n, Hash128 typeId, long nowUs, Hash128 src)
    {
        b.AddEntity(n.Id, n.Tier, typeId, src);
        if (!b.TrySeePhysicality(n.PhysId)) return;
        b.AddPhysicalityPreSeen(new PhysicalityRow(
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

    /// <summary>
    /// Rate a PLAYER on the result of one game — the aggregating lane for the one subject the
    /// rating math was literally invented for.
    ///
    /// A player's record was being computed as a query-time GROUP BY over every colour-header
    /// row in the corpus (~400k rows, ~10s), then cached with a TTL and a prewarm. That is a
    /// cache standing in for a missing fold: the consensus layer exists precisely so a strength
    /// estimate is READ, not recomputed. With this edge the record is one already-folded cell —
    /// witness_count is games played, eff_mu is the conservative strength — so the leaderboard
    /// is an indexed ORDER BY over a single relation partition and there is nothing to keep warm.
    ///
    /// It is also a strictly better number than the win percentage it replaces. Glicko-2 weighs
    /// who you beat, so a 68% score against 1960s grandmasters stops ranking level with 68%
    /// against beginners — and RD says how sure the corpus is, which a raw ratio cannot.
    ///
    /// Two edges, both aggregating, both deduped by content address:
    ///   (player, OUTCOME, Chess_Result)  — his overall strength, one cell per player
    ///   (player, PLAYED_BY, opponent)    — the head-to-head cell, folded per pairing
    /// contextId carries the game, so provenance stays per-game on the evidence rows while the
    /// consensus cells aggregate. PLAYED_BY was declared in the manifest and never emitted; this
    /// is the edge it was reserved for.
    /// </summary>
    public static void AppendPlayerResult(
        SubstrateChangeBuilder b, Hash128 player, Hash128? opponent, PlyOutcome outcome,
        double witnessWeight, Hash128 src, Hash128 gameId, int opponentElo = 0)
    {
        long sum = ScoreFp1e9(outcome);
        long? opponentRating = opponentElo > 0
            ? checked((long)opponentElo * Glicko2.FpScale)
            : null;
        b.AddAttestation(Outcome(
            player, games: 1, sum, witnessWeight, src, gameId, opponentRating));
        if (opponent is { } opp)
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: player,
                typeId: ChessVocabulary.PlayedByType,
                obj: opp,
                sourceId: src,
                contextId: gameId,
                games: 1,
                sumScoreFp1e9: sum,
                witnessWeight: witnessWeight,
                opponentRatingFp1e9: opponentRating));
    }

    private static AttestationRow Outcome(
        Hash128 subject, long games, long sum, double witnessWeight, Hash128 src,
        Hash128? contextId = null, long? opponentRatingFp1e9 = null) =>
        NativeAttestation.Aggregated(
            subject: subject,
            typeId: ChessVocabulary.OutcomeType,
            obj: ChessVocabulary.OutcomeObject,
            sourceId: src,
            contextId: contextId,
            games: games,
            sumScoreFp1e9: sum,
            witnessWeight: witnessWeight,
            opponentRatingFp1e9: opponentRatingFp1e9);

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
