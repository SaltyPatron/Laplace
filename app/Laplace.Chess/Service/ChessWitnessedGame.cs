using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Witnessed inputs for one playing of a line. <see cref="PlayingId"/> is the
/// attestation context / novelty unit — not the tournament <c>Chess_Event</c>.
/// </summary>
public sealed record ChessWitnessedGame(
    Hash128 LineId,
    Hash128 PlayingId,
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
    public Hash128 TrunkRootId => PlayingId;
}
