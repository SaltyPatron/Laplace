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
    public static void AppendMoveEdge(
        SubstrateChangeBuilder b, string fromKey, string toKey, PlyOutcome outcome,
        long games, double witnessWeight)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        if (games < 1) games = 1;
        long sum = checked(ScoreFp1e9(outcome) * games);   // `games` observations, all this result

        var from = EmitNodes(b, fromKey, nowUs);
        var to   = EmitNodes(b, toKey,   nowUs);

        // OUTCOME — credit the side to move at `from` (outcome already from that mover's perspective).
        foreach (var s in from.Substructures)
            b.AddAttestation(Outcome(s.Id, games, sum, witnessWeight));
        b.AddAttestation(Outcome(from.Position.Id, games, sum, witnessWeight));

        // MOVE — the exact-case edge from → to, scored by the same result.
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: from.Position.Id,
            typeId: ChessVocabulary.MoveType,
            obj: to.Position.Id,
            sourceId: ChessVocabulary.SourceId,
            contextId: null,
            games: games,
            sumScoreFp1e9: sum,
            witnessWeight: witnessWeight));
    }

    private static ChessComposed EmitNodes(SubstrateChangeBuilder b, string surface, long nowUs)
    {
        var c = ChessCompose.Position(surface);
        foreach (var s in c.Substructures) AddNode(b, s, ChessVocabulary.SubstructureType, nowUs);
        AddNode(b, c.Position, ChessVocabulary.PositionType, nowUs);
        return c;
    }

    private static void AddNode(SubstrateChangeBuilder b, in ChessNode n, Hash128 typeId, long nowUs)
    {
        b.AddEntity(n.Id, n.Tier, typeId, ChessVocabulary.SourceId);
        if (Environment.GetEnvironmentVariable("LAPLACE_CHESS_NOPHYS") == "1") return; // DIAGNOSTIC bisection
        bool noTraj = Environment.GetEnvironmentVariable("LAPLACE_CHESS_NOTRAJ") == "1"; // DIAGNOSTIC bisection
        b.AddPhysicality(new PhysicalityRow(
            Id:                n.PhysId,
            EntityId:          n.Id,
            SourceId:          ChessVocabulary.SourceId,
            Type:              PhysicalityType.Content,
            CoordX:            n.Coord[0], CoordY: n.Coord[1], CoordZ: n.Coord[2], CoordM: n.Coord[3],
            HilbertIndex:      n.Hb,
            TrajectoryXyzm:    noTraj ? System.Array.Empty<double>() : n.Trajectory,
            NConstituents:     noTraj ? 0 : n.NConstituents,
            AlignmentResidual: null,
            SourceDim:         null,
            ObservedAtUnixUs:  nowUs));
    }

    private static AttestationRow Outcome(Hash128 subject, long games, long sum, double witnessWeight) =>
        NativeAttestation.Aggregated(
            subject: subject,
            typeId: ChessVocabulary.OutcomeType,
            obj: ChessVocabulary.OutcomeObject,
            sourceId: ChessVocabulary.SourceId,
            contextId: null,
            games: games,
            sumScoreFp1e9: sum,
            witnessWeight: witnessWeight);
}
