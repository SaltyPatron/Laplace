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
/// like ChessAnalyze; GH #736: an engine verdict is a pure function of the position, so
/// the unit is the LINE — a second playing of an analyzed line re-deposits nothing
/// (folding the same engine verdict once per playing would be witness inflation of one
/// witness). One pass per line per Version regardless of depth — bumping Version is the
/// sanctioned re-run (re-running at a new depth without a version bump would
/// double-witness the same facts).
/// </summary>
public static class ChessStockfishEval
{
    public const int Version = 1;

    public const string SourceName = "ChessStockfish";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;

    public static Hash128 MarkerId(Hash128 lineId, int version)
        => Hash128.OfCanonical($"chess/stockfish-eval/{lineId}/{version}");

    private const double EvalWeight = 0.95;
    private const double QualityWeight = 0.9;

    internal sealed record PreparedLine(
        ChessWitnessedGame Game,
        ChessComposed?[] Positions,
        int?[] Evals,
        KeyValuePair<Hash128, int?>[] FreshEvaluations);

    public static string? ClassifyLoss(int lossCp) => lossCp switch
    {
        >= 300 => "blunder",
        >= 100 => "mistake",
        >= 50 => "inaccuracy",
        _ => null,
    };

    public static void DeriveGame(
        SubstrateChangeBuilder b, ChessWitnessedGame game, IPositionEvaluator eval,
        ConcurrentDictionary<Hash128, int?>? evalMemo = null)
    {
        var prepared = PrepareGame(game, eval, evalMemo, evalInflight: null);
        if (prepared is not null)
            DepositPrepared(b, prepared);
    }

    internal static PreparedLine? PrepareGame(
        ChessWitnessedGame game,
        IPositionEvaluator eval,
        ConcurrentDictionary<Hash128, int?>? evalMemo,
        ConcurrentDictionary<Hash128, Lazy<int?>>? evalInflight)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, m) is not { } start) return null;

        int n = game.Moves.Count;
        var evals = new int?[n + 1];
        var composed = new ChessComposed?[n + 1];
        var fresh = new List<KeyValuePair<Hash128, int?>>();

        var cur = start.Initial;
        ChessComposed? carried = null;
        for (int ply = 0; ply <= n; ply++)
        {
            var node = carried ?? ChessCompose.Position(cur.Board);
            composed[ply] = node;

            if (m.Terminal(cur) is null)
            {
                evals[ply] = EvaluatePosition(
                    node.Position.Id,
                    cur.Board.ToFen(),
                    eval,
                    evalMemo,
                    evalInflight,
                    out bool newlyCached);
                if (newlyCached)
                    fresh.Add(new KeyValuePair<Hash128, int?>(node.Position.Id, evals[ply]));
            }

            if (ply == n) break;
            var mv = San.Resolve(cur.Board, m.LegalActions(cur), game.Moves[ply]);
            if (mv is null) break;
            cur = m.Apply(cur, mv.Value);
            carried = ChessCompose.Position(cur.Board);
        }

        return new PreparedLine(game, composed, evals, fresh.ToArray());
    }

    internal static void DepositPrepared(SubstrateChangeBuilder b, PreparedLine prepared)
    {
        var game = prepared.Game;
        var composed = prepared.Positions;
        var evals = prepared.Evals;

        for (int ply = 0; ply < composed.Length; ply++)
        {
            if (composed[ply] is not { } node) continue;
            ChessGraph.EmitComposed(b, node, SourceId);
            if (evals[ply] is { } cp)
                ChessGraph.AppendEval(b, node, cp, games: 1, EvalWeight, SourceId, game.LineId);
        }

        int moveCount = Math.Min(game.Moves.Count, Math.Max(0, evals.Length - 1));
        for (int ply = 0; ply < moveCount; ply++)
        {
            if (evals[ply] is not { } before || evals[ply + 1] is not { } after) continue;
            if (composed[ply] is not { } from) continue;
            if (ClassifyLoss(before + after) is not { } token) continue;
            ChessGraph.AppendMoveQuality(
                b, from.Position.Id, token, games: 1, QualityWeight,
                SourceId, game.LineId);
        }

        b.AddEntity(MarkerId(game.LineId, Version), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddEntity(ChessVocabulary.AnalysisVersionMetaTypeId, EntityTier.Word,
                    BootstrapIntentBuilder.RelationTypeMetaTypeId, SourceId)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    game.LineId, ChessVocabulary.AnalysisVersionMetaTypeId, vId,
                    SourceId, contextId: null, ChessVocabulary.Trust));
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
            // Null means no observation was produced (timeout/dead process/etc.). Absence is
            // not a reusable engine verdict, so let a replacement engine try again later.
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
