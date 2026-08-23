namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Normalized session model every provider adapter parses into. ALL provider metadata
/// is retained: the typed fields cover the cross-provider universals; anything else a
/// format carries lands in <see cref="Meta"/> (session) / <see cref="AgentTurn.Meta"/>
/// (turn) as key=value pairs and is witnessed via HAS_ATTRIBUTE.
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
    /// Composed-turn count already witnessed by a prior ingest of this session (the
    /// deepest Agent_Session_Watermark the existence probe confirmed). Turns below it
    /// stage content only — no testimony — so re-ingesting a GROWN log never inflates
    /// observation counts for the already-witnessed prefix. 0 = witness everything.
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
/// Every relation the lane emits, as typed symbols. The canonical surface name is
/// DERIVED from the member name (HasRole → HAS_ROLE), so the only spelled-out roster
/// is <see cref="AgentTraceSource.Relations"/> (the declaration span the vocabulary
/// law exempts); emit sites never carry ad-hoc name literals (isa-gate g3).
/// </summary>
public enum AgentRelation
{
    AppearsIn,
    Precedes,
    HasAttribution,
    HasRole,
    AuthoredBy,
    Calls,
    IsInstanceOf,
    HasInput,
    HasResult,
    HasInputTokens,
    HasOutputTokens,
    HasCacheReadTokens,
    HasCacheCreateTokens,
    HasCost,
    HasStopReason,
    HasAttribute,
    HasName,
    HasContext,
    OnDate,
}

public static class AgentRelations
{
    private static readonly string[] Canonical = BuildCanonical();

    public static string Surface(AgentRelation relation) => Canonical[(int)relation];

    private static string[] BuildCanonical()
    {
        var names = Enum.GetNames<AgentRelation>();
        var result = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var sb = new System.Text.StringBuilder(names[i].Length + 4);
            foreach (char c in names[i])
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
                sb.Append(char.ToUpperInvariant(c));
            }
            result[i] = sb.ToString();
        }
        return result;
    }
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
