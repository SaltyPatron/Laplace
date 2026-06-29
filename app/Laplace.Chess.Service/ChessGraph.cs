using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Shared chess → substrate emission. Both self-play (<see cref="SubstrateTurnHost"/>) and PGN ingest
/// (<see cref="ChessPgnDecomposer"/>) compose positions the same way — converge, not fork.
///
/// A position is composed from its bounded SUBSTRUCTURE tokens (<see cref="ChessCompose"/>) directly via
/// the native merkle+centroid primitive — never routed through the universal text composer. Evidence is
/// attested at the right granularity: an <c>OUTCOME</c> on every substructure AND the position (so a
/// novel position inherits value from the substructures it shares with seen ones — the lookup-table
/// fix), plus the exact-case <c>MOVE</c> edge between the two positions.
/// </summary>
public static class ChessGraph
{
    /// <summary>Glicko score (×1e9) for the mover's per-ply credit: win 1.0 / draw 0.5 / loss 0.0.</summary>
    public static long ScoreFp1e9(PlyOutcome outcome) => outcome switch
    {
        PlyOutcome.Win  => 1_000_000_000L,
        PlyOutcome.Draw =>   500_000_000L,
        _               =>             0L,
    };

    /// <summary>
    /// Compose both endpoint positions from their substructures and record the evidence for one ply:
    /// <list type="bullet">
    /// <item>entities + physicalities for every substructure and both positions (id/coord/tier emergent);</item>
    /// <item>an <c>OUTCOME</c> attestation (scored by the result from the mover's perspective) on each
    ///   substructure of, and on, the <i>from</i> position — the side to move there made this move, so the
    ///   credit is theirs; every visited non-terminal position is credited once, as the <i>from</i> of its
    ///   outgoing move;</item>
    /// <item>one scored <c>MOVE</c> attestation <c>from → to</c> (the exact-case eval + move identity).</item>
    /// </list>
    /// Repeated substructures/positions/edges dedup within the batch.
    /// </summary>
    /// <summary>
    /// <paramref name="games"/> = the observation count (Glicko game-count) for this ply — the place we
    /// encode trust (Elo, confirmed-mate) WITHOUT touching the witness weight. The weight derives φ
    /// (opponent rd), which the fold requires to be CONSTANT per relation within a period; a substructure
    /// recurs across games of differing Elo, so varying the weight per game violates that invariant.
    /// Encoding trust in the game-count (a master/confirmed-mate game = more observations) gives the same
    /// up-weighting with a constant φ.
    /// </summary>
    /// <param name="sourceId">Provenance of this evidence (PGN corpus / self-play / openings / user). Null =
    ///   the legacy shared <see cref="ChessVocabulary.SourceId"/>, so existing callers are unchanged.</param>
    /// <param name="moverPlayerId">The named human mover, when known — emits a <c>PLAYED_BY</c> edge so the
    ///   move-choice is attributed. Null for openings/self-play (no named mover).</param>
    /// <param name="moveChoiceGames">Observation count for the MOVE edge, keyed on the MOVER's strength
    ///   ("Magnus's choice outweighs a weak player's"). ≤0 ⇒ falls back to <paramref name="games"/>, the
    ///   defender-weighted outcome count.</param>
    public static void AppendMoveEdge(
        SubstrateChangeBuilder b, string fromKey, string toKey, PlyOutcome outcome,
        long games, double witnessWeight,
        Hash128? sourceId = null, Hash128? moverPlayerId = null, long moveChoiceGames = 0,
        Hash128? contextId = null)
    {
        var src = sourceId ?? ChessVocabulary.SourceId;
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        if (games < 1) games = 1;
        if (moveChoiceGames < 1) moveChoiceGames = games;   // default: move-choice weight == outcome weight
        long sum = checked(ScoreFp1e9(outcome) * games);    // `games` observations, all this result

        var from = EmitNodes(b, fromKey, nowUs, src);
        var to   = EmitNodes(b, toKey,   nowUs, src);

        // OUTCOME — position quality, credited to the side to move at `from`, weighted by `games`
        // (defender-based: beating/holding a strong defender is stronger evidence). contextId ties each
        // witness to the GAME it came from (the conventional tier), without a per-ply edge.
        foreach (var s in from.Substructures)
            b.AddAttestation(Outcome(s.Id, games, sum, witnessWeight, src, contextId));
        b.AddAttestation(Outcome(from.Position.Id, games, sum, witnessWeight, src, contextId));

        // MOVE — the exact-case from→to edge. The CHOICE is the mover's, so it carries the mover-authority
        // observation count, not the defender's (a 2800's e4 here teaches more than a 1200's).
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

        // PLAYED_BY — attribute the move to its named mover, when present (openings/self-play have none).
        if (moverPlayerId is { } mover)
            b.AddAttestation(NativeAttestation.Categorical(
                from.Position.Id, "PLAYED_BY", mover, src, contextId, witnessWeight));

        // GAME_AT — the game entity explicitly lists the positions it visited, enabling the forward
        // query "all positions in game G" without scanning contextId attestations. Only emitted when
        // a named game entity exists; self-play and openings have no game node (contextId is null).
        if (contextId is { } gameId)
            b.AddAttestation(NativeAttestation.Categorical(
                gameId, "GAME_AT", from.Position.Id, src, contextId: null, witnessWeight));
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
        if (Environment.GetEnvironmentVariable("LAPLACE_CHESS_NOPHYS") == "1") return; // DIAGNOSTIC bisection
        bool noTraj = Environment.GetEnvironmentVariable("LAPLACE_CHESS_NOTRAJ") == "1"; // DIAGNOSTIC bisection
        b.AddPhysicality(new PhysicalityRow(
            Id:                n.PhysId,
            EntityId:          n.Id,
            SourceId:          src,
            Type:              PhysicalityType.Content,
            CoordX:            n.Coord[0], CoordY: n.Coord[1], CoordZ: n.Coord[2], CoordM: n.Coord[3],
            HilbertIndex:      n.Hb,
            TrajectoryXyzm:    noTraj ? System.Array.Empty<double>() : n.Trajectory,
            NConstituents:     noTraj ? 0 : n.NConstituents,
            AlignmentResidual: null,
            SourceDim:         null,
            ObservedAtUnixUs:  nowUs));
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
}
