using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Google Antigravity agent transcripts:
/// ~/.gemini/antigravity-cli/brain/&lt;conversationId&gt;/.system_generated/logs/transcript_full.jsonl.
/// Step records {step_index, source: USER_EXPLICIT|MODEL|SYSTEM, type, status, created_at,
/// content, thinking?, tool_calls?[{name,args}], exit_code?, error?}. Tool-typed steps
/// (RUN_COMMAND, VIEW_FILE, GREP_SEARCH, …) are invocations attached to the preceding
/// model turn. transcript.jsonl is a truncated view of the same steps — when its
/// transcript_full sibling exists, the truncated file yields nothing.
/// </summary>
public sealed class AntigravityAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "antigravity";

    private static readonly HashSet<string> ToolStepTypes = new(StringComparer.Ordinal)
    {
        "RUN_COMMAND", "VIEW_FILE", "GREP_SEARCH", "LIST_DIRECTORY", "CODE_ACTION",
        "SEARCH_WEB", "GENERIC", "CHECKPOINT",
    };

    public bool CanHandle(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (name is not ("transcript.jsonl" or "transcript_full.jsonl")) return false;
        var line = AdapterJson.FirstLine(filePath);
        return line is not null
            && line.Contains("\"step_index\"", StringComparison.Ordinal)
            && line.Contains("\"source\"", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".gemini", "antigravity-cli", "brain");
        yield return Path.Combine(homeDir, ".antigravity", "brain");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Path.GetFileName(filePath) == "transcript.jsonl"
            && File.Exists(Path.Combine(Path.GetDirectoryName(filePath)!, "transcript_full.jsonl")))
            yield break;

        var turns = new List<AgentTurn>();
        long firstUs = 0, lastUs = 0;
        int lastAssistantAt = -1;

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var step = doc.Root;
            long ts = AdapterJson.IsoUs(step.String("created_at"));
            if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }

            string type = step.String("type") ?? "GENERIC";
            string source = step.String("source") ?? "SYSTEM";
            string? content = step.String("content");
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            AdapterJson.CollectMeta(step, meta, "content", "thinking", "tool_calls", "created_at");

            if (ToolStepTypes.Contains(type) && source == "MODEL")
            {
                string? input = null;
                string name = type;
                var calls = step.Property("tool_calls");
                if (calls.IsArray)
                {
                    foreach (var c in calls.Items())
                    {
                        name = c.String("name") ?? type;
                        var args = c.Property("args");
                        input = args.IsValid ? args.RawText() : null;
                        break;
                    }
                }
                bool isError = step.Int64("exit_code") is { } rc && rc != 0;
                var call = new AgentToolCall(name, input, content, isError, ts);
                if (lastAssistantAt < 0)
                {
                    turns.Add(new AgentTurn(turns.Count, AgentRoles.Tool, ts) { Meta = meta });
                    lastAssistantAt = turns.Count - 1;
                }
                var owner = turns[lastAssistantAt];
                var list = new List<AgentToolCall>(owner.ToolCalls) { call };
                turns[lastAssistantAt] = owner with { ToolCalls = list };
                continue;
            }

            string role = type switch
            {
                "USER_INPUT" => AgentRoles.User,
                "PLANNER_RESPONSE" => AgentRoles.Assistant,
                "ERROR_MESSAGE" or "SYSTEM_MESSAGE" or "CONVERSATION_HISTORY" => AgentRoles.System,
                _ => AgentRoles.Normalize(source),
            };
            var turn = new AgentTurn(turns.Count, role, ts)
            {
                Text = content,
                Thinking = step.String("thinking"),
                Meta = meta,
            };
            if (role == AgentRoles.Assistant) lastAssistantAt = turns.Count;
            turns.Add(turn);
        }

        if (turns.Count == 0) yield break;

        // brain/<conversationId>/.system_generated/logs/transcript_full.jsonl
        string sessionKey = Path.GetFileNameWithoutExtension(filePath);
        var dir = new DirectoryInfo(filePath).Parent?.Parent?.Parent;
        if (dir is not null) sessionKey = dir.Name;

        yield return new AgentSession(ProviderKey, sessionKey, firstUs, lastUs, turns);
    }
}
