using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// OpenAI Codex CLI rollouts: ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl. Envelope
/// {timestamp, type, payload}: session_meta (identity/cwd), turn_context (model, effort),
/// response_item (message | reasoning | function_call(+_output) | custom_tool_call(+_output)),
/// event_msg (token_count carries per-turn usage). Reasoning summaries attach to the next
/// assistant message; tool outputs join their call by call_id.
/// </summary>
public sealed class CodexAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "codex";

    public bool CanHandle(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (name.StartsWith("rollout-", StringComparison.Ordinal)
            && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var line in AdapterJson.FirstLines(filePath, 4))
            if (line.Contains("\"session_meta\"", StringComparison.Ordinal)) return true;
        return false;
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".codex", "sessions");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
        var sessionMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionKey = null, cwd = null, model = null;
        string? pendingThinking = null;
        long firstUs = 0, lastUs = 0;
        int lastAssistantAt = -1;

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var root = doc.Root;
            long ts = AdapterJson.IsoUs(root.String("timestamp"));
            if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }
            var payload = root.Property("payload");
            if (!payload.IsObject) continue;

            switch (root.String("type"))
            {
                case "session_meta":
                    sessionKey = payload.String("session_id") ?? payload.String("id");
                    cwd = payload.String("cwd");
                    AdapterJson.CollectMeta(payload, sessionMeta,
                        "session_id", "id", "cwd", "timestamp", "base_instructions");
                    break;

                case "turn_context":
                    model = payload.String("model") ?? model;
                    if (payload.String("effort") is { } eff) sessionMeta["effort"] = eff;
                    if (payload.String("approval_policy") is { } ap)
                        sessionMeta["approval_policy"] = ap;
                    break;

                case "response_item":
                    HandleResponseItem(
                        payload, ts, turns, callsById, ref pendingThinking, ref lastAssistantAt, model);
                    break;

                case "event_msg":
                    if (payload.String("type") == "token_count" && lastAssistantAt >= 0)
                    {
                        var lastUse = payload.Property("info").Property("last_token_usage");
                        if (!lastUse.IsObject) break;
                        var usage = new AgentUsage(
                            lastUse.Int64("input_tokens"),
                            lastUse.Int64("output_tokens"),
                            lastUse.Int64("cached_input_tokens"),
                            CacheCreateTokens: null,
                            CostUsd: null);
                        var t = turns[lastAssistantAt];
                        if (t.Usage is null || t.Usage.IsEmpty)
                            turns[lastAssistantAt] = t with { Usage = usage };
                    }
                    break;
            }
        }

        if (turns.Count == 0) yield break;
        sessionKey ??= Path.GetFileNameWithoutExtension(filePath);
        yield return new AgentSession(ProviderKey, sessionKey, firstUs, lastUs, turns)
        {
            Cwd = cwd,
            Meta = sessionMeta,
        };
    }

    private static void HandleResponseItem(
        in JsonAstCursor payload,
        long ts,
        List<AgentTurn> turns,
        Dictionary<string, (int TurnAt, AgentToolCall Call)> callsById,
        ref string? pendingThinking,
        ref int lastAssistantAt,
        string? model)
    {
        switch (payload.String("type"))
        {
            case "message":
            {
                string role = AgentRoles.Normalize(payload.String("role"));
                var text = new StringBuilder();
                var content = payload.Property("content");
                if (content.IsArray)
                {
                    foreach (var block in content.Items())
                    {
                        if (block.String("type") is "input_text" or "output_text")
                        {
                            if (text.Length > 0) text.Append('\n');
                            text.Append(block.String("text"));
                        }
                    }
                }
                var turn = new AgentTurn(turns.Count, role, ts)
                {
                    Text = text.Length > 0 ? text.ToString() : null,
                    Model = role == AgentRoles.Assistant ? model : null,
                    Thinking = role == AgentRoles.Assistant ? pendingThinking : null,
                };
                if (role == AgentRoles.Assistant)
                {
                    pendingThinking = null;
                    lastAssistantAt = turns.Count;
                }
                turns.Add(turn);
                break;
            }
            case "reasoning":
            {
                // summary: [{type: summary_text, text}] — encrypted_content is opaque, skip.
                var summary = payload.Property("summary");
                if (summary.IsArray)
                {
                    var sb = new StringBuilder(pendingThinking ?? "");
                    foreach (var block in summary.Items())
                    {
                        if (block.String("text") is { Length: > 0 } t)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(t);
                        }
                    }
                    if (sb.Length > 0) pendingThinking = sb.ToString();
                }
                break;
            }
            case "function_call":
            case "custom_tool_call":
            {
                string name = payload.String("name") ?? "unknown";
                var args = payload.Property("arguments");
                string? input = args.Kind == JsonAstKind.String ? args.AsString()
                    : args.IsValid ? args.RawText()
                    : payload.String("input");
                var call = new AgentToolCall(name, input, null, false, ts);
                int owner = lastAssistantAt >= 0 ? lastAssistantAt : EnsureToolTurn(turns, ts);
                AppendCall(turns, owner, call);
                if (payload.String("call_id") is { } cid)
                    callsById[cid] = (owner, call);
                break;
            }
            case "function_call_output":
            case "custom_tool_call_output":
            {
                if (payload.String("call_id") is not { } cid
                    || !callsById.TryGetValue(cid, out var entry))
                    break;
                var output = payload.Property("output");
                string? result = output.Kind == JsonAstKind.String
                    ? output.AsString()
                    : output.IsValid ? output.RawText() : null;
                var filled = entry.Call with { ResultText = result };
                var owner = turns[entry.TurnAt];
                var list = new List<AgentToolCall>(owner.ToolCalls);
                int at = list.IndexOf(entry.Call);
                if (at >= 0)
                {
                    list[at] = filled;
                    turns[entry.TurnAt] = owner with { ToolCalls = list };
                    callsById[cid] = (entry.TurnAt, filled);
                }
                break;
            }
        }
    }

    private static int EnsureToolTurn(List<AgentTurn> turns, long ts)
    {
        turns.Add(new AgentTurn(turns.Count, AgentRoles.Tool, ts));
        return turns.Count - 1;
    }

    private static void AppendCall(List<AgentTurn> turns, int at, AgentToolCall call)
    {
        var t = turns[at];
        var list = new List<AgentToolCall>(t.ToolCalls) { call };
        turns[at] = t with { ToolCalls = list };
    }
}
