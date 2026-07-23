using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// CALCULATED stockfish eval pass (GH #573): replay a witnessed game, evaluate every
/// position with stockfish (side-to-move cp), attest HAS_EVAL deposits and eval-delta
/// MOVE_QUALITY classes under the ChessStockfish source. Versioned and marker-gated
/// like ChessAnalyze; one pass per game per Version regardless of depth — bumping
/// Version is the sanctioned re-run (re-running at a new depth without a version bump
/// would double-witness the same facts).
/// </summary>
public static class ChessStockfishEval
{
    public const int Version = 1;

    public const string SourceName = "ChessStockfish";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;

    public static Hash128 MarkerId(Hash128 gameId, int version)
        => Hash128.OfCanonical($"chess/stockfish-eval/{gameId}/{version}");

    private const double EvalWeight = 0.95;    // stronger witness than the in-repo search's 0.9
    private const double QualityWeight = 0.9;

    // Centipawns lost (mover POV) → canonical MoveQuality token; null = no class fires.
    // Silence is the non-event: a fine move deposits no quality row.
    public static string? ClassifyLoss(int lossCp) => lossCp switch
    {
        >= 300 => "blunder",
        >= 100 => "mistake",
        >= 50 => "inaccuracy",
        _ => null,
    };

    public static void DeriveGame(
        SubstrateChangeBuilder b, ChessWitnessedGame game, IPositionEvaluator eval,
        System.Collections.Concurrent.ConcurrentDictionary<Hash128, int?>? evalMemo = null)
    {
        var m = new ChessModality();
        var (state, _) = ChessAnalyze.InitialState(game.StartFen, m);

        // evals[i] = side-to-move cp of the position before ply i (plus the final position
        // at index N). Each position is evaluated exactly once; move i's loss reads i and i+1.
        int n = game.Moves.Count;
        var evals = new int?[n + 1];
        var composed = new ChessComposed?[n + 1];

        var cur = state;
        ChessComposed? carried = null;
        for (int ply = 0; ply <= n; ply++)
        {
            var node = carried ?? ChessGraph.EmitComposed(b, m.StateKey(cur), SourceId);
            composed[ply] = node;
            bool terminal = m.Terminal(cur) is not null;
            // Positions are content-addressed and shared across games (the start position
            // recurs in every standard game) — a stockfish value is a pure function of the
            // position, so the run-level memo searches each unique position ONCE and every
            // repeat reads the cached cp. Deposits stay per-game (provenance unchanged).
            if (terminal)
                evals[ply] = null;
            else if (evalMemo is not null && evalMemo.TryGetValue(node.Position.Id, out var cached))
                evals[ply] = cached;
            else
            {
                evals[ply] = eval.EvaluateCp(cur.Board.ToFen());
                // Cap defends the bounded-cache law; middlegame positions rarely recur, so
                // the high-value opening entries are long since resident when the cap hits.
                if (evalMemo is not null && evalMemo.Count < 3_000_000)
                    evalMemo[node.Position.Id] = evals[ply];
            }

            if (evals[ply] is { } cp)
                ChessGraph.AppendEval(b, node, cp, games: 1, EvalWeight, SourceId, game.GameId);

            if (ply == n) break;
            var mv = San.Resolve(cur.Board, m.LegalActions(cur), game.Moves[ply]);
            if (mv is null) break; // unreplayable movetext — stop, no marker withheld: partial evals stand
            cur = m.Apply(cur, mv.Value);
            carried = ChessGraph.EmitComposed(b, m.StateKey(cur), SourceId);
        }

        for (int ply = 0; ply < n; ply++)
        {
            // Mover's eval after their move is the negation of the next position's
            // side-to-move eval; loss = before − (−after) = before + after.
            if (evals[ply] is not { } before || evals[ply + 1] is not { } after) continue;
            if (ClassifyLoss(before + after) is not { } token) continue;
            ChessGraph.AppendMoveQuality(
                b, composed[ply]!.Position.Id, token, games: 1, QualityWeight,
                SourceId, game.GameId);
        }

        b.AddEntity(MarkerId(game.GameId, Version), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddAttestation(NativeAttestation.Categorical(
                game.GameId, "ANALYZED_AT", vId, SourceId, null, ChessVocabulary.Trust));
    }
}
