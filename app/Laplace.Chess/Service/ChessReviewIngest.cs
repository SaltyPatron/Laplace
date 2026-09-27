using Laplace.Decomposers.Abstractions;
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
        // Sorted: EnumerateFiles returns filesystem order, and batch boundaries (hence which
        // repeats working-set dedup absorbs) would otherwise depend on it.
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pgn", SearchOption.AllDirectories)
                       .OrderBy(static p => p, StringComparer.Ordinal)
            : [path];
        foreach (var file in files)
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

        // The shared PGN parse: TryParseGame replays the mainline and composes the line and
        // event; the review's per-position judgments carry the playing (event) as context.
        if (ChessPgnDecomposer.TryParseGame(gameText) is not { } parsed) return;
        var eventId = parsed.PlayingId;
        var src = ChessVocabulary.ReviewSourceId;

        var state = m.Initial();
        int ply = 0;
        foreach (var plyStream in parsed.Walk.Mainline)
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
                // A blunder is a move-quality judgment, not game-outcome testimony: it is attested
                // as MOVE_QUALITY, never into the OUTCOME cell real game results fold into.
                if (MoveQuality.FromReviewTag(w.Tag) is { } q)
                    ChessGraph.AppendMoveQuality(b, fromKey, q, QualityGames, ReviewWitnessWeight, src, eventId);
            }

            state = m.Apply(state, mv.Value);
            ply++;
        }
    }
}
