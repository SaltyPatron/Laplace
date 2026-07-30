using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Witnessed-layer inputs for <see cref="ChessAnalyze.DeriveGame"/> — hydrated from the
/// substrate (content roundtrip on the playing's verbatim HAS_MOVETEXT, re-parsed for
/// per-ply tokens), not from PGN files. GH #736: <see cref="LineId"/> is the game CONTENT
/// entity (shared across every playing of the same moves); <see cref="EventId"/> is THIS
/// playing (the attestation context every per-playing fact was recorded under).
/// </summary>
public sealed record ChessWitnessedGame(
    Hash128 LineId,
    Hash128 EventId,
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
    public Hash128 TrunkRootId => EventId;
}
