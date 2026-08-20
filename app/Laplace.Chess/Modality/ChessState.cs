using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

public sealed class ChessState
{
    public Board Board { get; }

    public ImmutableList<Hash128> RepetitionHistory { get; }

    public ChessState(Board board, ImmutableList<Hash128>? repetitionHistory = null)
    {
        Board = board;
        RepetitionHistory = repetitionHistory ?? ImmutableList<Hash128>.Empty;
    }
}
