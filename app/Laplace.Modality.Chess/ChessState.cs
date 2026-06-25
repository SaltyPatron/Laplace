using System.Collections.Immutable;

namespace Laplace.Modality.Chess;

/// <summary>
/// Immutable chess state. Wraps the mutable <see cref="Board"/> (never mutated after construction by
/// the modality — <see cref="ChessModality.Apply"/> clones before making a move) plus a repetition
/// history of canonical 4-field position keys since the last irreversible move (pawn move / capture),
/// sufficient to detect threefold repetition. The history and the halfmove/fullmove counters are NOT
/// part of <see cref="ChessModality.StateKey"/>.
/// </summary>
public sealed class ChessState
{
    public Board Board { get; }

    /// <summary>Canonical 4-field keys reached since the last irreversible move, INCLUDING this one.</summary>
    public ImmutableList<string> RepetitionHistory { get; }

    public ChessState(Board board, ImmutableList<string>? repetitionHistory = null)
    {
        Board = board;
        RepetitionHistory = repetitionHistory ?? ImmutableList<string>.Empty;
    }
}
