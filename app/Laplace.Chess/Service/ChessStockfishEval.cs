using System.Collections.Concurrent;
using System.Threading;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// CALCULATED stockfish eval pass (GH #573): replay a witnessed line, evaluate every
/// position with stockfish (side-to-move cp), attest HAS_EVAL deposits and eval-delta
/// MOVE_QUALITY classes under the ChessStockfish source. Versioned and marker-gated
/// like ChessAnalyze; GH #736: each complete FEN is evaluated under one exact recipe.
/// The unit is the LINE — another playing of an analyzed line under that recipe re-deposits nothing.
/// </summary>
public static class ChessStockfishEval
{
    public const int Version = 2;

    public const string SourceName = "ChessStockfish";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;

    public static Hash128 MarkerId(Hash128 lineId, int version)
        => Hash128.OfCanonical($"chess/stockfish-eval/{lineId}/{version}");

    internal static string MarkerKey(Hash128 lineId, StockfishEvaluationRecipe recipe)
        => recipe.MarkerKey(lineId.ToString());

    public static Hash128 MarkerId(Hash128 lineId, StockfishEvaluationRecipe recipe)
        => Hash128.OfCanonical(MarkerKey(lineId, recipe));

    internal static string InputKey(string fen, StockfishEvaluationRecipe recipe)
        => recipe.InputKey(fen);

    private const double EvalWeight = 0.95;
    private const double QualityWeight = 0.9;

    internal sealed record PreparedLine(
        ChessWitnessedGame Game,
        StockfishEvaluationRecipe Recipe,
        ChessComposed?[] Positions,
        int?[] Evals,
        KeyValuePair<Hash128, int?>[] FreshEvaluations,
        bool Complete);

    public static string? ClassifyLoss(int lossCp) => lossCp switch
    {
        >= 300 => "blunder",
        >= 100 => "mistake",
        >= 50 => "inaccuracy",
        _ => null,
    };

    public static void DeriveGame(
        SubstrateChangeBuilder b, ChessWitnessedGame game, IPositionEvaluator eval,
        StockfishEvaluationRecipe recipe,
        ConcurrentDictionary<Hash128, int?>? evalMemo = null)
    {
        var prepared = PrepareGame(game, eval, recipe, evalMemo, evalInflight: null);
        if (prepared is not null)
            DepositPrepared(b, prepared);
    }

    /// <summary>
    /// Pure preparation. Successful per-position searches may be cached immediately, but the
    /// line is not publishable until every reachable non-terminal position returned a verdict
    /// and the movetext replay reached its declared end. This makes a line the atomic testimony
    /// boundary: a timeout cannot mint a completion marker over a partial census.
    /// </summary>
    internal static PreparedLine? PrepareGame(
        ChessWitnessedGame game,
        IPositionEvaluator eval,
        StockfishEvaluationRecipe recipe,
        ConcurrentDictionary<Hash128, int?>? evalMemo,
        ConcurrentDictionary<Hash128, Lazy<int?>>? evalInflight)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, m) is not { } start) return null;

        int n = game.Moves.Count;
        var evals = new int?[n + 1];
        var composed = new ChessComposed?[n + 1];
        var fresh = new List<KeyValuePair<Hash128, int?>>();
        bool complete = true;

        var cur = start.Initial;
        ChessComposed? carried = null;
        for (int ply = 0; ply <= n; ply++)
        {
            var node = carried ?? ChessCompose.Position(cur.Board);
            composed[ply] = node;

            if (m.Terminal(cur) is null)
            {
                string fen = cur.Board.ToFen();
                var evaluationInput = Hash128.OfCanonical(InputKey(fen, recipe));
                evals[ply] = EvaluatePosition(
                    evaluationInput, fen,
                    eval,
                    evalMemo,
                    evalInflight,
                    out bool newlyCached);
                if (newlyCached)
                    fresh.Add(new KeyValuePair<Hash128, int?>(evaluationInput, evals[ply]));
                if (!evals[ply].HasValue)
                    complete = false;
            }

            if (ply == n) break;
            var mv = San.Resolve(cur.Board, m.LegalActions(cur), game.Moves[ply]);
            if (mv is null)
            {
                complete = false;
                break;
            }
            cur = m.Apply(cur, mv.Value);
            carried = ChessCompose.Position(cur.Board);
        }

        return new PreparedLine(game, recipe, composed, evals, fresh.ToArray(), complete);
    }

    /// <summary>
    /// Serial publication. Partial engine work lives only in the derived eval cache and is
    /// retried/reused on the next pass; it never becomes partial substrate testimony and never
    /// receives the line completion marker.
    /// </summary>
    internal static void DepositPrepared(SubstrateChangeBuilder b, PreparedLine prepared,
        Hash128? recipeMetadataRoot = null)
    {
        if (!prepared.Complete) return;

        var game = prepared.Game;
        var composed = prepared.Positions;
        var evals = prepared.Evals;
        var context = MarkerId(game.LineId, prepared.Recipe);

        for (int ply = 0; ply < composed.Length; ply++)
        {
            if (composed[ply] is not { } node) continue;
            ChessGraph.EmitComposed(b, node, SourceId);
            if (evals[ply] is { } cp)
                ChessGraph.AppendEval(b, node, cp, games: 1, EvalWeight, SourceId, context);
        }

        int moveCount = Math.Min(game.Moves.Count, Math.Max(0, evals.Length - 1));
        for (int ply = 0; ply < moveCount; ply++)
        {
            if (evals[ply] is not { } before || evals[ply + 1] is not { } after) continue;
            if (composed[ply] is not { } from) continue;
            if (ClassifyLoss(before + after) is not { } token) continue;
            ChessGraph.AppendMoveQuality(
                b, from.Position.Id, token, games: 1, QualityWeight,
                SourceId, context);
        }

        b.AddEntity(context, EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
        // The existing source family remains addressable. Each new calculated context names
        // its exact inputs; legacy anonymous v1 contexts and their evidence are left intact.
        if ((recipeMetadataRoot ?? ContentEmitter.Emit(b, prepared.Recipe.CanonicalManifest, SourceId)) is { } vId)
            b.AddEntity(ChessVocabulary.AnalysisVersionMetaTypeId, EntityTier.Word,
                    BootstrapIntentBuilder.RelationTypeMetaTypeId, SourceId)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    game.LineId, ChessVocabulary.AnalysisVersionMetaTypeId, vId,
                    SourceId, contextId: context, ChessVocabulary.Trust));
    }

    private static int? EvaluatePosition(
        Hash128 positionId,
        string fen,
        IPositionEvaluator eval,
        ConcurrentDictionary<Hash128, int?>? evalMemo,
        ConcurrentDictionary<Hash128, Lazy<int?>>? evalInflight,
        out bool newlyCached)
    {
        newlyCached = false;
        if (evalMemo is not null && evalMemo.TryGetValue(positionId, out var cached))
            return cached;

        if (evalMemo is null || evalInflight is null)
        {
            int? value = eval.EvaluateCp(fen);
            if (value.HasValue && evalMemo is not null && evalMemo.TryAdd(positionId, value))
                newlyCached = true;
            return value;
        }

        var candidate = new Lazy<int?>(
            () => eval.EvaluateCp(fen),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = evalInflight.GetOrAdd(positionId, candidate);
        try
        {
            int? value = shared.Value;
            if (value.HasValue && evalMemo.TryAdd(positionId, value))
                newlyCached = true;
            return value;
        }
        finally
        {
            if (ReferenceEquals(shared, candidate))
                evalInflight.TryRemove(positionId, out _);
        }
    }
}
