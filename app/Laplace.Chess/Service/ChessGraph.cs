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
    // Ply-grain GAME_AT / GAME_AT_PLY emitters were removed (and retired from
    // ChessSeedManifest — GH #577): at that grain they can never corroborate across
    // games, so each was a permanently single-witness consensus cell. The game's
    // verbatim HAS_MOVETEXT plus replay reconstructs them; contextId keeps
    // per-game provenance on the evidence rows.
    //
    // PLAYED_BY at PLAYER grain is the opposite case and is emitted (AppendPlayerResult): one
    // cell per pairing, so two players who met 28 times corroborate into a single cell with 28
    // witnesses. The grain is what decides whether an edge can accumulate, not the name.
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

        // ctx = null, on purpose — the same rule RecordOpeningHeaders states for line-grain
        // facts: "each playing that asserts the same [fact] for the same line MERGES into one
        // evidence row whose observation count accumulates."
        //
        // The attestation id is blake3(subject | type | object | source | context)
        // (attestation_engine.c:110-132), so ctx = the playing made every one of these rows
        // unique BY CONSTRUCTION. Identical content — the same position, the same move —
        // could never merge, and the row count scaled with positions x games instead of
        // saturating with the vocabulary. Measured: 280 OUTCOME rows per game over 1,193
        // chess.com games, and marginal rows per game still RISING at game 190,705 (429 ->
        // 552), which a closed vocabulary cannot do.
        //
        // Nothing is lost with ctx dropped. These are the AGGREGATING lane: they are read as
        // eff_mu / rd / witness_count, i.e. "how do games through this position fare", which
        // IS the merged cell — witness_count becomes times-seen instead of always 1. The
        // per-playing record lives where it belongs: the line's trajectory carries which
        // positions this game passed through, and the event carries who played it.
        foreach (var s in from.Substructures)
            b.AddAttestation(Outcome(s.Id, games, sum, witnessWeight, sourceId, contextId: null));
        b.AddAttestation(Outcome(from.Position.Id, games, sum, witnessWeight, sourceId, contextId: null));

        long moveSum = checked(ScoreFp1e9(outcome) * moveChoiceGames);
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: from.Position.Id,
            typeId: ChessVocabulary.MoveType,
            obj: to.Position.Id,
            sourceId: sourceId,
            contextId: null,
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
    /// GH #736 position-grain motif: (position, HAS_MOTIF, motif concept), ctx = null so
    /// every game that reaches the position corroborates the SAME cell — the
    /// shared-content sibling of the line-grain GAME_HAS_MOTIF (one family).
    /// </summary>
    internal static void AppendPositionMotif(
    SubstrateChangeBuilder b, Hash128 positionId, string motif, double witnessWeight, Hash128 sourceId)
    {
        if (ContentEmitter.Emit(b, motif, sourceId) is not { } mid) return;
        b.AddAttestation(NativeAttestation.Categorical(positionId, "HAS_MOTIF", mid, sourceId, null, witnessWeight));
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
        if (b.PresenceOracle?.IsProvenPresent(c.Position.Id) == true) return c;

        foreach (var s in c.Substructures) AddNode(b, s, ChessVocabulary.SubstructureType, nowUs, src);
        AddNode(b, c.Position, ChessVocabulary.PositionType, nowUs, src);
        return c;
    }

    /// <summary>
    /// The GAME TRAJECTORY (spec 11 §2): one GeometryZM linestring on the game entity whose
    /// vertices are the positions it passed through, in order, with the position ids bit-packed
    /// into the mantissa channel. Chess-trajectory parity with the model lane — a game deposits
    /// its whole move sequence as ONE geometric object, exactly as a circuit deposits its whole
    /// relational assertion as one testimony-packed linestring.
    ///
    /// This is what the ply sequence was missing. Positions were already resident and geometric,
    /// and every game's line could be reconstructed by replaying its movetext — but the line
    /// itself was not an object, so "games near this line", maneuver search and transposition
    /// detection had nothing to index. With it they are GiST-backed spatial queries.
    ///
    /// Calculated layer, deliberately: this is deposited under the analyzer's source, versioned
    /// and evictable, never confused with the verbatim movetext the recorder transcribes. And it
    /// is a PHYSICALITY, never part of the game's id — geometry is identity and reconstruction,
    /// not semantics, and coord/hilbert equality is not identity above tier 0.
    /// </summary>
    public static void AppendGameTrajectory(
        SubstrateChangeBuilder b, Hash128 gameId, IReadOnlyList<ChessNode> line, Hash128 src, long nowUs)
    {
        if (line.Count == 0) return;

        var ids = new Hash128[line.Count];
        var coords = new double[(long)line.Count * 4];
        for (int i = 0; i < line.Count; i++)
        {
            ids[i] = line[i].Id;
            coords[i * 4 + 0] = line[i].Coord[0];
            coords[i * 4 + 1] = line[i].Coord[1];
            coords[i * 4 + 2] = line[i].Coord[2];
            coords[i * 4 + 3] = line[i].Coord[3];
        }

        // Same primitives the position tier composes with — one implementation of "pack an
        // ordered id sequence into a trajectory", not a chess-specific second one.
        double[] traj = Trajectory.Build(ids);
        // Karcher, not Centroid — intrinsic mean, lands on S3 at norm 1. See
        // NgramTrajectory for the measurement. Requires a reseed.
        double[] centroid = Math4d.KarcherMean(coords);

        b.AddPhysicality(new PhysicalityRow(
            Id: PhysicalityId.Compute(gameId, PhysicalityType.Content),
            EntityId: gameId,
            SourceId: src,
            Type: PhysicalityType.Content,
            CoordX: centroid[0], CoordY: centroid[1], CoordZ: centroid[2], CoordM: centroid[3],
            HilbertIndex: Hilbert128.Encode(centroid),
            TrajectoryXyzm: traj,
            NConstituents: line.Count,
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
    /// <summary>
    /// GH #736: the line's own fold cell — (line, OUTCOME, Chess_Result), one witness per
    /// playing, white-POV score. witness_count IS "times this line was played"; eff_mu IS
    /// how the line fares. ctx = the playing-event, so evidence stays per-playing.
    /// </summary>
    public static void AppendLineOutcome(
        SubstrateChangeBuilder b, Hash128 lineId, PlyOutcome whitePov,
        double witnessWeight, Hash128 src, Hash128 eventId)
        => b.AddAttestation(Outcome(lineId, games: 1, ScoreFp1e9(whitePov), witnessWeight, src, eventId));

    public static void AppendPlayerResult(
        SubstrateChangeBuilder b, Hash128 player, Hash128? opponent, PlyOutcome outcome,
        double witnessWeight, Hash128 src, Hash128 gameId)
    {
        long sum = ScoreFp1e9(outcome);
        b.AddAttestation(Outcome(player, games: 1, sum, witnessWeight, src, gameId));
        if (opponent is { } opp)
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: player,
                typeId: ChessVocabulary.PlayedByType,
                obj: opp,
                sourceId: src,
                contextId: gameId,
                games: 1,
                sumScoreFp1e9: sum,
                witnessWeight: witnessWeight));
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
