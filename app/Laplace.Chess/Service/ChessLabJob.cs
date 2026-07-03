using System.Text.Json.Serialization;

namespace Laplace.Chess.Service;

[JsonConverter(typeof(JsonStringEnumConverter))]
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

[JsonConverter(typeof(JsonStringEnumConverter))]
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

public sealed record ChessLabJob(
    string Id,
    ChessLabJobKind Kind,
    ChessLabJobState State,
    IReadOnlyDictionary<string, string> Config,
    ChessLabJobSummary Summary,
    IReadOnlyDictionary<string, string> Artifacts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null);
