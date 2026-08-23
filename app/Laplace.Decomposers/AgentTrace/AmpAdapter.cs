using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Amp (Sourcegraph) threads: ~/.local/share/amp/threads/T-&lt;uuid&gt;.json — one JSON
/// object per thread: {v, id:"T-…", created (epoch ms), title, messages[{role,
/// content: string|blocks, usage?{inputTokens, outputTokens,
/// cacheCreationInputTokens, cacheReadInputTokens}}], …}. Blocks: text / thinking /
/// tool_use{id,name,input} / tool_result{tool_use_id|toolUseID, content}. Some
/// versions persist only text blocks — both shapes accepted.
/// </summary>
public sealed class AmpAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "amp";

    public bool CanHandle(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (!name.StartsWith("T-", StringComparison.Ordinal)
            || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        var head = AdapterJson.Head(filePath, 256);
        return head is not null
            && head.Contains("\"id\"", StringComparison.Ordinal)
            && head.Contains("\"T-", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".local", "share", "amp", "threads");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(filePath, ct));
        if (doc is null) yield break;
        var root = doc.Root;
        var messages = root.Property("messages");
        if (!messages.IsArray) yield break;

        long createdUs = AdapterJson.MsUs(root.Int64("created"));
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
        long lastUs = createdUs;

        foreach (var m in messages.Items())
        {
            string role = AgentRoles.Normalize(m.String("role"));
            long ts = AdapterJson.MsUs(m.Property("meta").Int64("sentAt"));
            if (ts == 0) ts = createdUs;
            else lastUs = Math.Max(lastUs, ts);

            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var calls = new List<AgentToolCall>();
            bool resultOnly = true;

            var content = m.Property("content");
            if (content.Kind == JsonAstKind.String)
            {
                text.Append(content.AsString());
                resultOnly = false;
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
                            resultOnly = false;
                            break;
                        case "thinking":
                            if (thinking.Length > 0) thinking.Append('\n');
                            thinking.Append(block.String("thinking"));
                            resultOnly = false;
                            break;
                        case "tool_use":
                        {
                            var input = block.Property("input");
                            var call = new AgentToolCall(
                                block.String("name") ?? "unknown",
                                input.IsValid ? input.RawText() : null, null, false, ts);
                            calls.Add(call);
                            resultOnly = false;
                            if (block.String("id") is { } cid)
                                callsById[cid] = (turns.Count, call);
                            break;
                        }
                        case "tool_result":
                        {
                            string? cid = block.String("tool_use_id") ?? block.String("toolUseID");
                            if (cid is null || !callsById.TryGetValue(cid, out var entry)) break;
                            var rc = block.Property("content");
                            string? result = rc.Kind == JsonAstKind.String ? rc.AsString()
                                : rc.IsValid ? rc.RawText() : null;
                            var filled = entry.Call with { ResultText = result };
                            int inCurrent = calls.IndexOf(entry.Call);
                            if (inCurrent >= 0) calls[inCurrent] = filled;
                            else if (entry.TurnAt < turns.Count)
                            {
                                var owner = turns[entry.TurnAt];
                                var list = new List<AgentToolCall>(owner.ToolCalls);
                                int at = list.IndexOf(entry.Call);
                                if (at >= 0)
                                {
                                    list[at] = filled;
                                    turns[entry.TurnAt] = owner with { ToolCalls = list };
                                }
                            }
                            callsById[cid] = (entry.TurnAt, filled);
                            break;
                        }
                    }
                }
            }

            if (resultOnly && text.Length == 0 && thinking.Length == 0 && calls.Count == 0)
                continue;

            var u = m.Property("usage");
            AgentUsage? usage = u.IsObject
                ? new AgentUsage(
                    u.Int64("inputTokens"), u.Int64("outputTokens"),
                    u.Int64("cacheReadInputTokens"), u.Int64("cacheCreationInputTokens"),
                    CostUsd: null)
                : null;

            turns.Add(new AgentTurn(turns.Count, role, ts)
            {
                Text = text.Length > 0 ? text.ToString() : null,
                Thinking = thinking.Length > 0 ? thinking.ToString() : null,
                Usage = usage is { IsEmpty: false } ? usage : null,
                ToolCalls = calls,
            });
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(
            ProviderKey,
            AgentTraceEmitter.SanitizeKey(root.String("id")
                ?? Path.GetFileNameWithoutExtension(filePath)),
            createdUs, lastUs, turns)
        {
            Title = root.String("title"),
        };
    }
}
