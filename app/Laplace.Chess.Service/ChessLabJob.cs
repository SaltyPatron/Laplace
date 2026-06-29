namespace Laplace.Chess.Service;

public enum ChessLabJobKind
{
    SubstrateTest,
    Ladder,
    Tactics,
    Review,
    LearnedPst,
    Cutechess,
    LichessBot,
    LichessFetch,
}

public enum ChessLabJobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ChessLabJobSummary(
    int Done = 0,
    int Total = 0,
    string? Message = null);

/// <summary>Snapshot of a Chess Lab job (substrate-test, ladder, cutechess gauntlet, …).</summary>
public sealed record ChessLabJob(
    string Id,
    ChessLabJobKind Kind,
    ChessLabJobState State,
    IReadOnlyDictionary<string, string> Config,
    ChessLabJobSummary Summary,
    IReadOnlyDictionary<string, string> Artifacts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null);
