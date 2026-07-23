using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Witnessed-layer inputs for <see cref="ChessAnalyze.DeriveGame"/> — hydrated from the
/// substrate (content roundtrip on the game's verbatim HAS_MOVETEXT, re-parsed for per-ply
/// tokens), not from PGN files.
/// </summary>
public sealed record ChessWitnessedGame(
    Hash128 GameId,
    IReadOnlyList<string> Moves,
    GameOutcome Result,
    Hash128? WhitePlayer,
    Hash128? BlackPlayer,
    string? StartFen,
    string?[]? ClockTokens,
    string?[]? EvalTokens,
    string?[]? QualityTokens,
    double[]? SpentSeconds = null)
    : ITrunkRootRecord
{
    public Hash128 TrunkRootId => GameId;
}
