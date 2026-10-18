using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Opening-catalog continuations, loaded once from the ECO move text and shared
/// as the fallback of a per-search table. The first line to claim a position keeps
/// its move. Load order is longest line first.
/// </summary>
internal static class OpeningContinuationBook
{
    private static readonly Lazy<SearchContinuationTable> Book = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SearchContinuationTable Shared => Book.Value;

    private static SearchContinuationTable Load()
    {
        var table = new SearchContinuationTable();
        var modality = new ChessModality();
        var start = modality.Initial().Board;
        foreach (var (_, _, sans) in OpeningClassifier.LoadLines(OpeningSeed.DefaultDir))
        {
            var state = modality.Initial();
            var moves = new List<ChessMove>(sans.Count);
            bool parsed = true;
            foreach (var san in sans)
            {
                var move = San.Resolve(state.Board, modality.LegalActions(state), san);
                if (move is null)
                {
                    parsed = false;
                    break;
                }
                moves.Add(move.Value);
                state = modality.Apply(state, move.Value);
            }
            if (parsed && moves.Count > 0)
                table.SeedOpening(start, moves);
        }
        return table;
    }
}
