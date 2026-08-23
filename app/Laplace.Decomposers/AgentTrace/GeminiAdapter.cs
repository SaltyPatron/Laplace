using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Gemini CLI (and its Qwen Code fork): ~/.gemini/tmp/&lt;projectHash&gt;/chats/session-*.json.
/// One JSON document per session: {sessionId, projectHash, startTime, lastUpdated, summary,
/// messages[{id, timestamp, type: user|gemini|info, content, model?, thoughts?, tokens?,
/// toolCalls?[{id, name, args, status, result, resultDisplay, timestamp}]}]}.
/// </summary>
public sealed class GeminiAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "gemini";

    public bool CanHandle(string filePath)
    {
        if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.GetFileName(filePath).StartsWith("session-", StringComparison.Ordinal)
            && filePath.Contains("chats", StringComparison.Ordinal))
            return true;
        var head = AdapterJson.Head(filePath);
        return head is not null
            && head.Contains("\"sessionId\"", StringComparison.Ordinal)
            && head.Contains("\"projectHash\"", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".gemini", "tmp");
        yield return Path.Combine(homeDir, ".qwen", "tmp");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
        using var doc = JsonAstDocument.TryParse(bytes);
        if (doc is null) yield break;
        var root = doc.Root;
        var messages = root.Property("messages");
        if (!root.IsObject || !messages.IsArray) yield break;

        var sessionMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        AdapterJson.CollectMeta(root, sessionMeta,
            "messages", "sessionId", "startTime", "lastUpdated", "summary");

        var turns = new List<AgentTurn>();
        foreach (var m in messages.Items())
        {
            long ts = AdapterJson.IsoUs(m.String("timestamp"));
            string role = AgentRoles.Normalize(m.String("type"));
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            AdapterJson.CollectMeta(m, meta,
                "content", "timestamp", "type", "model", "thoughts", "tokens", "toolCalls");

            AgentUsage? usage = null;
            var tok = m.Property("tokens");
            if (tok.IsObject)
            {
                usage = new AgentUsage(
                    tok.Int64("input"),
                    tok.Int64("output"),
                    tok.Int64("cached"),
                    CacheCreateTokens: null,
                    CostUsd: null);
                if (tok.Int64("thoughts") is { } th)
                    meta["thoughts_tokens"] = th.ToString(CultureInfo.InvariantCulture);
                if (tok.Int64("tool") is { } tt)
                    meta["tool_tokens"] = tt.ToString(CultureInfo.InvariantCulture);
            }

            var calls = new List<AgentToolCall>();
            var toolCalls = m.Property("toolCalls");
            if (toolCalls.IsArray)
            {
                foreach (var c in toolCalls.Items())
                {
                    string name = c.String("name") ?? "unknown";
                    var args = c.Property("args");
                    var r = c.Property("result");
                    string? result = r.Kind == JsonAstKind.String ? r.AsString()
                        : r.IsValid ? r.RawText()
                        : c.String("resultDisplay");
                    bool isError = c.String("status") is "error" or "failed";
                    calls.Add(new AgentToolCall(
                        name, args.IsValid ? args.RawText() : null, result, isError,
                        AdapterJson.IsoUs(c.String("timestamp"))));
                }
            }

            turns.Add(new AgentTurn(turns.Count, role, ts)
            {
                Text = m.String("content"),
                Thinking = m.String("thoughts"),
                Model = m.String("model"),
                Usage = usage,
                ToolCalls = calls,
                Meta = meta,
            });
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(
            Provider: filePath.Contains($"{Path.DirectorySeparatorChar}.qwen{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ? "qwen" : ProviderKey,
            SessionKey: root.String("sessionId") ?? Path.GetFileNameWithoutExtension(filePath),
            StartedAtUnixUs: AdapterJson.IsoUs(root.String("startTime")),
            EndedAtUnixUs: AdapterJson.IsoUs(root.String("lastUpdated")),
            Turns: turns)
        {
            Title = root.String("summary"),
            Meta = sessionMeta,
        };
    }
}
