using Laplace.Decomposers.Abstractions;
namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Normalized session every provider adapter parses into. Typed fields cover what all
/// providers share; any other metadata lands in <see cref="Meta"/> (session) or
/// <see cref="AgentTurn.Meta"/> (turn) and is attested as a [key, value] under HAS_ATTRIBUTE.
/// </summary>
public sealed record AgentSession(
    string Provider,
    string SessionKey,
    long StartedAtUnixUs,
    long EndedAtUnixUs,
    IReadOnlyList<AgentTurn> Turns)
{
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? GitBranch { get; init; }
    /// <summary>User/account identity when the log carries one (e.g. Copilot's gh login).</summary>
    public string? UserKey { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } =
        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Composed-turn count already witnessed by a prior ingest (the deepest
    /// Agent_Session_Watermark the existence probe found). Turns below it stage content
    /// without attestations, so a grown log does not re-count its prefix. 0 = witness all.
    /// </summary>
    public int WitnessedTurnWatermark { get; init; }
}

/// <summary>One conversational turn. Ordinal is position in the session, 0-based.</summary>
public sealed record AgentTurn(
    int Ordinal,
    string Role,
    long TimestampUnixUs)
{
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public string? Model { get; init; }
    public string? StopReason { get; init; }
    public AgentUsage? Usage { get; init; }
    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];
    public IReadOnlyDictionary<string, string> Meta { get; init; } =
        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
}

public sealed record AgentToolCall(
    string Name,
    string? InputJson,
    string? ResultText,
    bool IsError,
    long TimestampUnixUs);

public sealed record AgentUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheCreateTokens,
    double? CostUsd)
{
    public bool IsEmpty =>
        InputTokens is null && OutputTokens is null && CacheReadTokens is null
        && CacheCreateTokens is null && CostUsd is null;
}

/// <summary>
/// Every relation agent-session ingest attests, as typed symbols. The surface name derives
/// from the member name (HasRole → HAS_ROLE), so the only spelled-out roster is
/// <see cref="AgentTraceSource.Relations"/>; emit sites carry no name literals.
/// </summary>
public enum AgentRelation
{
    Role,
    Model,
    Title,
    User,
    StopReason,
    IsError,
    GitBranch,
    InputTokens,
    OutputTokens,
    CacheReadInputTokens,
    CacheCreationInputTokens,
    CostUsd,
}

public static class AgentRelations
{
    public static string Surface(AgentRelation relation) => relation switch
    {
        AgentRelation.Role => "role",
        AgentRelation.Model => "model",
        AgentRelation.Title => "title",
        AgentRelation.User => "user",
        AgentRelation.StopReason => "stop_reason",
        AgentRelation.IsError => "is_error",
        AgentRelation.GitBranch => "gitBranch",
        AgentRelation.InputTokens => "input_tokens",
        AgentRelation.OutputTokens => "output_tokens",
        AgentRelation.CacheReadInputTokens => "cache_read_input_tokens",
        AgentRelation.CacheCreationInputTokens => "cache_creation_input_tokens",
        AgentRelation.CostUsd => "cost_usd",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };
}

/// <summary>Canonical role vocabulary; adapters normalize provider-local names into these.</summary>
public static class AgentRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
    public const string Tool = "tool";

    public static string Normalize(string? role) => role?.ToLowerInvariant() switch
    {
        "user" or "human" or "user_explicit" => User,
        "assistant" or "model" or "gemini" or "ai" or "planner_response" => Assistant,
        "system" or "developer" or "system_message" or "info" => System,
        "tool" or "function" or "tool_result" => Tool,
        _ => System,
    };
}
