using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Witnessed-layer inputs for <see cref="ChessAnalyze.DeriveGame"/> — hydrated from the
/// substrate (content roundtrip on HAS_MOVETEXT / ply attestations), not re-parsed PGN.
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
    string?[]? QualityTokens)
    : ITrunkRootRecord
{
    public Hash128 TrunkRootId => GameId;
}
