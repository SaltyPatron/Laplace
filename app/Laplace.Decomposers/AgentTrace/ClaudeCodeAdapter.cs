using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Claude Code transcripts: ~/.claude/projects/&lt;project-slug&gt;/&lt;sessionId&gt;.jsonl.
/// One file = one session. Records are envelopes {type, uuid, parentUuid, sessionId,
/// timestamp, cwd, gitBranch, version, message} where message is the Anthropic API
/// shape (role + content blocks: text / thinking / tool_use / tool_result). tool_result
/// blocks arrive inside LATER user-typed records and are joined to their tool_use by id.
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "claude-code";

    public bool CanHandle(string filePath)
    {
        if (!filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && filePath.Contains("projects", StringComparison.Ordinal))
            return true;
        foreach (var line in AdapterJson.FirstLines(filePath, 8))
        {
            if (line.Contains("\"sessionId\"", StringComparison.Ordinal)
                && line.Contains("\"parentUuid\"", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".claude", "projects");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, AgentToolCall>(StringComparer.Ordinal);
        var callOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        var sessionMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionKey = null, cwd = null, gitBranch = null;
        long firstUs = 0, lastUs = 0;

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var root = doc.Root;
            string? type = root.String("type");
            if (type is not ("user" or "assistant")) continue;
            var message = root.Property("message");
            if (!message.IsObject) continue;

            long ts = AdapterJson.IsoUs(root.String("timestamp"));
            if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }
            sessionKey ??= root.String("sessionId");
            cwd ??= root.String("cwd");
            gitBranch ??= root.String("gitBranch");
            if (root.String("version") is { } v) sessionMeta["version"] = v;

            string role = AgentRoles.Normalize(message.String("role") ?? type);
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var turnCalls = new List<AgentToolCall>();
            bool sawToolResult = false;

            var content = message.Property("content");
            if (content.Kind == JsonAstKind.String)
            {
                text.Append(content.AsString());
            }
            else if (content.IsArray)
            {
                foreach (var block in content.Items())
                {
                    switch (block.String("type"))
                    {
                        case "text":
                            if (text.Length > 0) text.Append('\n');
                            text.Append(block.String("text"));
                            break;
                        case "thinking":
                            if (thinking.Length > 0) thinking.Append('\n');
                            thinking.Append(block.String("thinking"));
                            break;
                        case "tool_use":
                        {
                            string name = block.String("name") ?? "unknown";
                            var input = block.Property("input");
                            var call = new AgentToolCall(
                                name, input.IsValid ? input.RawText() : null, null, false, ts);
                            turnCalls.Add(call);
                            if (block.String("id") is { } cid)
                            {
                                callsById[cid] = call;
                                callOwner[cid] = turns.Count;
                            }
                            break;
                        }
                        case "tool_result":
                        {
                            sawToolResult = true;
                            string? cid = block.String("tool_use_id");
                            if (cid is null || !callsById.TryGetValue(cid, out var call)) break;
                            string? result = FlattenResult(block.Property("content"));
                            bool isError = block.Bool("is_error") == true;
                            var filled = call with { ResultText = result, IsError = isError };
                            ReplaceCall(turns, callOwner[cid], call, filled, turnCalls);
                            callsById[cid] = filled;
                            break;
                        }
                    }
                }
            }

            // A record that is ONLY tool_result plumbing is not a conversational turn of
            // its own — its payload was joined onto the owning assistant turn's call above.
            if (sawToolResult && text.Length == 0 && thinking.Length == 0 && turnCalls.Count == 0)
                continue;

            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            AdapterJson.CollectMeta(root, meta,
                "type", "message", "timestamp", "sessionId", "session_id", "uuid",
                "parentUuid", "cwd", "gitBranch", "version");
            AgentUsage? usage = null;
            var u = message.Property("usage");
            if (u.IsObject)
            {
                usage = new AgentUsage(
                    u.Int64("input_tokens"),
                    u.Int64("output_tokens"),
                    u.Int64("cache_read_input_tokens"),
                    u.Int64("cache_creation_input_tokens"),
                    CostUsd: null);
            }

            turns.Add(new AgentTurn(turns.Count, role, ts)
            {
                Text = text.Length > 0 ? text.ToString() : null,
                Thinking = thinking.Length > 0 ? thinking.ToString() : null,
                Model = message.String("model"),
                StopReason = message.String("stop_reason"),
                Usage = usage,
                ToolCalls = turnCalls,
                Meta = meta,
            });
        }

        if (turns.Count == 0) yield break;
        sessionKey ??= Path.GetFileNameWithoutExtension(filePath);
        yield return new AgentSession(ProviderKey, sessionKey, firstUs, lastUs, turns)
        {
            Cwd = cwd,
            GitBranch = gitBranch,
            Meta = sessionMeta,
        };
    }

    private static string? FlattenResult(in JsonAstCursor content)
    {
        if (content.Kind == JsonAstKind.String) return content.AsString();
        if (!content.IsArray) return content.IsValid ? content.RawText() : null;
        var sb = new StringBuilder();
        foreach (var block in content.Items())
        {
            if (block.String("type") == "text")
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(block.String("text"));
            }
        }
        return sb.Length > 0 ? sb.ToString() : content.RawText();
    }

    /// <summary>Swap an already-listed call for its result-filled version, wherever it lives.</summary>
    private static void ReplaceCall(
        List<AgentTurn> turns, int ownerOrdinal, AgentToolCall old, AgentToolCall filled,
        List<AgentToolCall> currentTurnCalls)
    {
        int inCurrent = currentTurnCalls.IndexOf(old);
        if (inCurrent >= 0) { currentTurnCalls[inCurrent] = filled; return; }
        if (ownerOrdinal >= turns.Count) return;
        var owner = turns[ownerOrdinal];
        var list = new List<AgentToolCall>(owner.ToolCalls);
        int at = list.IndexOf(old);
        if (at < 0) return;
        list[at] = filled;
        turns[ownerOrdinal] = owner with { ToolCalls = list };
    }
}
