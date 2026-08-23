using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Agent session-log lane (spec 34 batch counterpart). One decomposer, many provider
/// format adapters (Claude Code, Codex, Gemini, Antigravity, Copilot, Cursor, generic
/// JSON/JSONL fallback). This static source witnesses the STRUCTURAL rows (roles,
/// tool graph, usage scalars, metadata attributes); conversational content and
/// membership ride the per-tenant UserPrompt@/Response@/ToolResult@ sources so
/// replayed logs land on the same evidence cells as live turns (TurnCloser parity).
/// </summary>
public readonly struct AgentTraceSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("AgentTraceDecomposer");

    public static string SourceName => "AgentTraceDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AgentTranscript");

    /// <summary>
    /// Every relation the lane emits under ANY of its sources (the HAS_POS law).
    /// Turn ORDER is deliberately absent: sequence lives in the session physicality
    /// trajectory (Pillar 3a), not in adjacency attestations. PRECEDES appears only
    /// as the live lane's corroborating prompt→reply cell.
    /// </summary>
    public static IReadOnlyList<string> Relations { get; } =
    [
        "APPEARS_IN",
        "PRECEDES",
        "HAS_ATTRIBUTION",
        "HAS_ROLE",
        "AUTHORED_BY",
        "CALLS",
        "IS_INSTANCE_OF",
        "HAS_INPUT",
        "HAS_RESULT",
        "HAS_INPUT_TOKENS",
        "HAS_OUTPUT_TOKENS",
        "HAS_CACHE_READ_TOKENS",
        "HAS_CACHE_CREATE_TOKENS",
        "HAS_COST",
        "HAS_STOP_REASON",
        "HAS_ATTRIBUTE",
        "HAS_NAME",
        "HAS_CONTEXT",
        "ON_DATE",
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
