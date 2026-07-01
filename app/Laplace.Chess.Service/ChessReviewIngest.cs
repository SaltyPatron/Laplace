using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public static class ChessReviewIngest
{
    private const double ReviewWitnessWeight = 0.4;
    private const long QualityGames = 1;

    public static int IngestPath(SubstrateChangeBuilder b, ChessModality m, string path, int depth = 4)
    {
        int n = 0;
        foreach (var file in Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.pgn", SearchOption.AllDirectories) : [path])
        {
            foreach (var gameText in PgnGames.StreamGames(file))
            {
                IngestGameText(b, m, gameText, depth);
                n++;
            }
        }
        return n;
    }

    public static void IngestGameText(SubstrateChangeBuilder b, ChessModality m, string gameText, int depth = 4)
    {
        if (ChessGameReview.ReviewGameText(gameText, depth) is not { } reviewed) return;
        if (reviewed.Worst.Count == 0) return;

        var bytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult stream;
        using (var ast = GrammarDecomposer.Parse(bytes, "pgn"))
            stream = PgnMovetext.Walk(ast, bytes);
        if (stream.Result is null || stream.Mainline.Count == 0) return;

        var moves = stream.Mainline.Select(p => p.San).ToList();
        string date = PgnGames.TagStr(gameText, "Date");
        var gameId = ChessVocabulary.GameId(
            PgnGames.TagStr(gameText, "White"), PgnGames.TagStr(gameText, "Black"), date, moves);
        var src = ChessVocabulary.ReviewSourceId;

        var state = m.Initial();
        int ply = 0;
        foreach (var plyStream in stream.Mainline)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), plyStream.San);
            if (mv is null) break;
            string fromKey = m.StateKey(state);
            bool white = state.Board.WhiteToMove;
            int moveNo = (ply / 2) + 1;
            string uci = mv.Value.ToUci();

            foreach (var w in reviewed.Worst)
            {
                if (w.MoveNo != moveNo || w.White != white || w.Played != uci) continue;
                if (MoveQuality.FromReviewTag(w.Tag) is { } q)
                    ChessGraph.AppendMoveQuality(b, fromKey, q, QualityGames, ReviewWitnessWeight, src, gameId);
                if (w.CpLoss >= ChessGameReview.BlunderCp)
                    EmitBlunderOutcome(b, fromKey, src, gameId);
            }

            state = m.Apply(state, mv.Value);
            ply++;
        }
    }

    private static void EmitBlunderOutcome(SubstrateChangeBuilder b, string fromKey, Hash128 src, Hash128 gameId)
    {
        long games = 1;
        long sum = ChessGraph.ScoreFp1e9(PlyOutcome.Loss) * games;
        var composed = ChessCompose.Position(fromKey);
        foreach (var s in composed.Substructures)
            b.AddAttestation(NativeAttestation.Aggregated(
                s.Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject,
                src, gameId, games, sum, ReviewWitnessWeight));
        b.AddAttestation(NativeAttestation.Aggregated(
            composed.Position.Id, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject,
            src, gameId, games, sum, ReviewWitnessWeight));
    }
}
