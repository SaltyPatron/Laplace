using System.Collections.Immutable;

namespace Laplace.Modality.Chess;

public sealed class ChessState
{
    public Board Board { get; }

    public ImmutableList<string> RepetitionHistory { get; }

    public ChessState(Board board, ImmutableList<string>? repetitionHistory = null)
    {
        Board = board;
        RepetitionHistory = repetitionHistory ?? ImmutableList<string>.Empty;
    }
}
