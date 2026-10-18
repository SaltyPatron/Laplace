using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

/// <summary>
/// Cache of witnessed continuations for one search. Keys are
/// <see cref="ChessPositionIdentity"/> values, the same position identity the
/// game already uses. The table does not mint a second identity.
/// Seed it before <see cref="Search.Think"/> and do not mutate it during a search.
/// Search uses <see cref="Entry.PreferredMove"/> to order interior nodes.
/// <see cref="Entry.WitnessedEvaluationCp"/> is stored for the leaf blend and is
/// not added again at every ply.
/// </summary>
public sealed class SearchContinuationTable
{
    /// <summary>Largest witnessed-evaluation nudge a leaf blend may apply.</summary>
    public const int BlendCapCp = 512;

    public readonly record struct Entry(
        ChessMove? ActiveGameMove,
        ChessMove? OpeningMove,
        int? WitnessedEvaluationCp)
    {
        /// <summary>The game being played outranks the opening catalog.</summary>
        public ChessMove? PreferredMove => ActiveGameMove ?? OpeningMove;
    }

    private readonly Dictionary<Hash128, Entry> _entries = new();

    public bool TryGet(Hash128 positionId, out Entry entry)
        => _entries.TryGetValue(positionId, out entry);

    public void SeedActiveGame(Board start, IReadOnlyList<ChessMove> line)
        => SeedLine(start, line, activeGame: true);

    public void SeedOpening(Board start, IReadOnlyList<ChessMove> line)
        => SeedLine(start, line, activeGame: false);

    public void SeedWitnessedEvaluation(Board position, int centipawnsFromSideToMove)
    {
        Hash128 id = ChessPositionIdentity.PositionId(position);
        Entry current = _entries.GetValueOrDefault(id);
        _entries[id] = current with { WitnessedEvaluationCp = centipawnsFromSideToMove };
    }

    private void SeedLine(Board start, IReadOnlyList<ChessMove> line, bool activeGame)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(line);
        var board = start.Clone();
        foreach (var move in line)
        {
            Hash128 id = ChessPositionIdentity.PositionId(board);
            Entry current = _entries.GetValueOrDefault(id);
            _entries[id] = activeGame
                ? current with { ActiveGameMove = move }
                : current with { OpeningMove = move };
            MoveApply.Make(board, move);
        }
    }
}
