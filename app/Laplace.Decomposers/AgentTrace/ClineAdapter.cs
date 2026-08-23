using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Cline and Roo Code task stores (same family, distinct globalStorage ids):
/// &lt;globalStorage&gt;/{saoudrizwan.claude-dev | rooveterinaryinc.roo-cline}/tasks/&lt;taskId&gt;/
/// api_conversation_history.json — an Anthropic MessageParam[] (Roo adds ts/condense
/// bookkeeping onto each message). The sibling ui_messages.json carries usage inside
/// `say:"api_req_started"` rows (`text` = JSON {tokensIn,tokensOut,cacheWrites,
/// cacheReads,cost}); task_metadata.json carries per-request model ids
/// (model_usage[{ts,model_id}]). The api history file is the claimed witness unit;
/// siblings are read for metadata.
/// </summary>
public sealed class ClineAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "cline";

    public bool CanHandle(string filePath)
    {
        if (Path.GetFileName(filePath) != "api_conversation_history.json") return false;
        var head = AdapterJson.Head(filePath, 512);
        return head is not null && head.TrimStart().StartsWith('[')
            && head.Contains("\"role\"", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        foreach (var code in (string[])["Code", "Code - Insiders", "VSCodium", "Cursor"])
        {
            yield return Path.Combine(homeDir, ".config", code, "User", "globalStorage",
                "saoudrizwan.claude-dev", "tasks");
            yield return Path.Combine(homeDir, ".config", code, "User", "globalStorage",
                "rooveterinaryinc.roo-cline", "tasks");
        }
        yield return Path.Combine(homeDir, ".cline", "data", "tasks");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        string taskDir = Path.GetDirectoryName(filePath)!;
        string taskId = new DirectoryInfo(taskDir).Name;
        string provider = filePath.Contains("roo-cline", StringComparison.OrdinalIgnoreCase)
            ? "roo-code" : ProviderKey;

        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(filePath, ct));
        if (doc is null || !doc.Root.IsArray) yield break;

        // Sidecars: per-request usage (ui_messages api_req_started) and model ids.
        var usageQueue = await ReadUsageAsync(Path.Combine(taskDir, "ui_messages.json"), ct);
        string? model = await ReadModelAsync(Path.Combine(taskDir, "task_metadata.json"), ct);

        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
        int usageAt = 0;
        long firstUs = 0, lastUs = 0;

        foreach (var m in doc.Root.Items())
        {
            string role = AgentRoles.Normalize(m.String("role"));
            long ts = AdapterJson.MsUs(m.Int64("ts")); // Roo only; Cline has none
            if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }

            var text = new StringBuilder();
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
                            if (block.String("tool_use_id") is not { } cid
                                || !callsById.TryGetValue(cid, out var entry))
                                break;
                            var rc = block.Property("content");
                            string? result = rc.Kind == JsonAstKind.String ? rc.AsString()
                                : rc.IsValid ? rc.RawText() : null;
                            var filled = entry.Call with { ResultText = result };
                            var owner = turns.Count > entry.TurnAt ? turns[entry.TurnAt] : null;
                            int inCurrent = calls.IndexOf(entry.Call);
                            if (inCurrent >= 0) calls[inCurrent] = filled;
                            else if (owner is not null)
                            {
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

            if (resultOnly && text.Length == 0 && calls.Count == 0) continue;
            AgentUsage? usage = null;
            if (role == AgentRoles.Assistant && usageAt < usageQueue.Count)
                usage = usageQueue[usageAt++];
            turns.Add(new AgentTurn(turns.Count, role, ts)
            {
                Text = text.Length > 0 ? text.ToString() : null,
                Model = role == AgentRoles.Assistant ? model : null,
                Usage = usage,
                ToolCalls = calls,
            });
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(
            provider, AgentTraceEmitter.SanitizeKey(taskId), firstUs, lastUs, turns);
    }

    /// <summary>ui_messages.json say:"api_req_started" rows, in order — one per API request.</summary>
    private static async Task<List<AgentUsage>> ReadUsageAsync(string uiPath, CancellationToken ct)
    {
        var result = new List<AgentUsage>();
        if (!File.Exists(uiPath)) return result;
        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(uiPath, ct));
        if (doc is null || !doc.Root.IsArray) return result;
        foreach (var m in doc.Root.Items())
        {
            if (m.String("say") != "api_req_started") continue;
            if (m.String("text") is not { } payload) continue;
            using var info = JsonAstDocument.TryParse(payload);
            if (info is null) continue;
            var u = info.Root;
            var usage = new AgentUsage(
                u.Int64("tokensIn"), u.Int64("tokensOut"),
                u.Int64("cacheReads"), u.Int64("cacheWrites"),
                u.Property("cost").AsDouble());
            if (!usage.IsEmpty) result.Add(usage);
        }
        return result;
    }

    private static async Task<string?> ReadModelAsync(string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath)) return null;
        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(metaPath, ct));
        if (doc is null) return null;
        var usage = doc.Root.Property("model_usage");
        if (!usage.IsArray) return null;
        string? model = null;
        foreach (var row in usage.Items())
            model = row.String("model_id") ?? model; // last one wins: the task's final model
        return model;
    }
}
