using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Shared chess → substrate emission. Both self-play (<see cref="SubstrateTurnHost"/>) and PGN ingest
/// (<see cref="ChessPgnDecomposer"/>) build MOVE edges the same way — converge, not fork.
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
    /// Compose both endpoint positions as content entities (id/tier/coord emergent, IS_TYPED_AS
    /// Chess_Position) and add one MOVE attestation scored by the game result from the mover's
    /// perspective. Repeated positions/edges dedup within the batch.
    /// </summary>
    public static void AppendMoveEdge(
        SubstrateChangeBuilder b, string fromKey, string toKey, PlyOutcome outcome, double witnessWeight)
    {
        var subj = CategoryAnchor.Emit(
            b, fromKey, ChessVocabulary.PositionType, ChessVocabulary.SourceId, ChessVocabulary.Trust);
        var obj = CategoryAnchor.Emit(
            b, toKey, ChessVocabulary.PositionType, ChessVocabulary.SourceId, ChessVocabulary.Trust);
        if (subj is null || obj is null) return;

        b.AddAttestation(NativeAttestation.Aggregated(
            subject: subj.Value,
            typeId: ChessVocabulary.MoveType,
            obj: obj.Value,
            sourceId: ChessVocabulary.SourceId,
            contextId: null,
            games: 1,
            sumScoreFp1e9: ScoreFp1e9(outcome),
            witnessWeight: witnessWeight));
    }
}
