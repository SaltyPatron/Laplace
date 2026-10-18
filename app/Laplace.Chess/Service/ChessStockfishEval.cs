using System.Collections.Concurrent;
using System.Threading;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Versioned calculation witness over recorded lines: replays a witnessed line, evaluates
/// every position with Stockfish under one exact recipe (side-to-move centipawns), and attests
/// HAS_EVAL on the position, the recommended move on that position, and the played move's
/// evaluation change and MOVE_QUALITY on the transition. The unit is the line: its marker is
/// keyed by line and recipe, so another playing of an analyzed line deposits nothing.
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
        bool Complete,
        string?[] RecommendedUci,
        Hash128?[] PlayedTransitions);

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
        ConcurrentDictionary<Hash128, int?>? evalMemo = null,
        ConcurrentDictionary<Hash128, string>? recommendations = null)
    {
        var prepared = PrepareGame(game, eval, recipe, evalMemo, evalInflight: null, recommendations);
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
        ConcurrentDictionary<Hash128, Lazy<int?>>? evalInflight,
        ConcurrentDictionary<Hash128, string>? recommendations = null)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, m, game.Rules) is not { } start) return null;

        int n = game.Moves.Count;
        var evals = new int?[n + 1];
        var composed = new ChessComposed?[n + 1];
        var recommended = new string?[n + 1];
        var transitions = new Hash128?[n];
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
                var verdict = EvaluatePosition(
                    evaluationInput, fen,
                    eval,
                    evalMemo,
                    evalInflight,
                    recommendations,
                    out bool newlyCached);
                evals[ply] = verdict?.Centipawns;
                recommended[ply] = LegalRecommendation(m, cur, verdict?.RecommendedUci);
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
            transitions[ply] = ChessCompose.TransitionKey(
                node.Position.Id, ChessCompose.MoveId(cur.Board.MovingPiece(mv.Value), mv.Value));
            cur = m.Apply(cur, mv.Value);
            carried = ChessCompose.Position(cur.Board);
        }

        return new PreparedLine(game, recipe, composed, evals, fresh.ToArray(), complete, recommended, transitions);
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
            if (prepared.RecommendedUci[ply] is { Length: > 0 } uci
                && ContentEmitter.Emit(b, "chess/stockfish-recommended/" + uci, SourceId) is { } recommendedId)
                b.AddAttestation(NativeAttestation.Categorical(
                    node.Position.Id, "HAS_FEATURE", recommendedId, SourceId, context, EvalWeight));
        }

        int moveCount = Math.Min(game.Moves.Count, Math.Max(0, evals.Length - 1));
        for (int ply = 0; ply < moveCount; ply++)
        {
            if (evals[ply] is not { } before || evals[ply + 1] is not { } after) continue;
            if (prepared.PlayedTransitions[ply] is not { } transition) continue;
            int loss = before + after;
            ChessGraph.AppendEvaluationChange(b, transition, loss, games: 1, EvalWeight, SourceId, context);
            if (ClassifyLoss(loss) is not { } token) continue;
            ChessGraph.AppendMoveQuality(
                b, transition, token, games: 1, QualityWeight,
                SourceId, context);
        }

        b.AddEntity(context, EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType);
        // The calculation context names its exact inputs: the recipe manifest is content,
        // attested against the line under the analysis-version meta type.
        if ((recipeMetadataRoot ?? ContentEmitter.Emit(b, prepared.Recipe.CanonicalManifest, SourceId)) is { } vId)
            b.AddEntity(ChessVocabulary.AnalysisVersionMetaTypeId, EntityTier.Word,
                    BootstrapIntentBuilder.RelationTypeMetaTypeId)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    game.LineId, ChessVocabulary.AnalysisVersionMetaTypeId, vId,
                    SourceId, contextId: context, ChessVocabulary.Trust));
        else
            throw new InvalidDataException("Stockfish evaluation recipe could not be admitted as content");
        IngestUnitCompletion.Emit(b, context, SourceId, 22);
    }

    private static string? LegalRecommendation(ChessModality modality, ChessState state, string? uci)
    {
        if (string.IsNullOrWhiteSpace(uci)) return null;
        foreach (var candidate in modality.LegalActions(state))
            if (candidate.ToUci() == uci) return uci;
        return null;
    }

    private static PositionVerdict? EvaluatePosition(
        Hash128 positionId,
        string fen,
        IPositionEvaluator eval,
        ConcurrentDictionary<Hash128, int?>? evalMemo,
        ConcurrentDictionary<Hash128, Lazy<int?>>? evalInflight,
        ConcurrentDictionary<Hash128, string>? recommendations,
        out bool newlyCached)
    {
        newlyCached = false;
        if (evalMemo is not null && evalMemo.TryGetValue(positionId, out var cached))
        {
            string? advised = null;
            recommendations?.TryGetValue(positionId, out advised);
            return cached is int cp ? new PositionVerdict(cp, advised) : null;
        }

        if (evalMemo is null || evalInflight is null)
        {
            var verdict = eval.EvaluateVerdict(fen);
            if (verdict is { } found && evalMemo is not null && evalMemo.TryAdd(positionId, found.Centipawns))
            {
                newlyCached = true;
                if (found.RecommendedUci is { Length: > 0 } uci)
                    recommendations?.TryAdd(positionId, uci);
            }
            return verdict;
        }

        var candidate = new Lazy<int?>(() =>
        {
            var verdict = eval.EvaluateVerdict(fen);
            if (verdict?.RecommendedUci is { Length: > 0 } uci)
                recommendations?.TryAdd(positionId, uci);
            return verdict?.Centipawns;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = evalInflight.GetOrAdd(positionId, candidate);
        try
        {
            int? value = shared.Value;
            if (value is int cp && evalMemo.TryAdd(positionId, cp))
                newlyCached = true;
            string? advised = null;
            recommendations?.TryGetValue(positionId, out advised);
            return value is int scored ? new PositionVerdict(scored, advised) : null;
        }
        finally
        {
            if (ReferenceEquals(shared, candidate))
                evalInflight.TryRemove(positionId, out _);
        }
    }
}
