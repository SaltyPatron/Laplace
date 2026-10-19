using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Source identity for agent session logs. It witnesses the structural rows (roles, tool
/// graph, usage, metadata attributes); conversational content and membership are attested
/// under the per-tenant UserPrompt@/Response@/ToolResult@ sources, so replayed logs land
/// in the same consensus cells as live turns.
/// </summary>
public readonly struct AgentTraceSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("AgentTraceDecomposer");

    public static string SourceName => "AgentTraceDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("AgentTranscript");

    /// <summary>
    /// Every relation agent-session ingest attests under any of its sources. Turn order is
    /// absent: sequence lives in the session's physicality trajectory, not in attestations.
    /// </summary>
    public static IReadOnlyList<string> Relations { get; } =
    [
        "role",
        "model",
        "title",
        "user",
        "stop_reason",
        "is_error",
        "gitBranch",
        "input_tokens",
        "output_tokens",
        "cache_read_input_tokens",
        "cache_creation_input_tokens",
        "cost_usd",
    ];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
    [
        "Conversation_Session",
        "Conversation_Turn",
        "Agent_Tool",
        "Agent_Model",
        "Tool_Invocation",
        "Agent_Session_Watermark",
    ];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
