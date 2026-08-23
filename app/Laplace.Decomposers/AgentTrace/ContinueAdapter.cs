using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Continue.dev sessions: ~/.continue/sessions/&lt;uuid&gt;.json — {sessionId, title,
/// workspaceDirectory, history:[{message{role, content, toolCalls?[{id, function:
/// {name, arguments}}], usage?}, contextItems, toolCallStates?[{toolCallId, status,
/// output?}], reasoning?{text}, ...}], usage?{promptTokens, completionTokens,
/// totalCost}}. Roles include "thinking" and "tool"; messages carry no timestamps
/// (only the index's dateCreated). The sessions.json index is not a session.
/// </summary>
public sealed class ContinueAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "continue";

    public bool CanHandle(string filePath)
    {
        if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.GetFileName(filePath) == "sessions.json") return false;
        if (!filePath.Contains($"{Path.DirectorySeparatorChar}sessions{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            return false;
        var head = AdapterJson.Head(filePath, 256);
        return head is not null
            && head.Contains("\"sessionId\"", StringComparison.Ordinal)
            && head.Contains("\"history\"", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".continue", "sessions");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(filePath, ct));
        if (doc is null) yield break;
        var root = doc.Root;
        var history = root.Property("history");
        if (!history.IsArray) yield break;

        var turns = new List<AgentTurn>();
        foreach (var item in history.Items())
        {
            var m = item.Property("message");
            if (!m.IsObject) continue;
            string rawRole = m.String("role") ?? "system";
            string role = rawRole == "thinking" ? AgentRoles.Assistant : AgentRoles.Normalize(rawRole);

            var text = new StringBuilder();
            var content = m.Property("content");
            if (content.Kind == JsonAstKind.String) text.Append(content.AsString());
            else if (content.IsArray)
            {
                foreach (var part in content.Items())
                {
                    if (part.String("type") == "text" && part.String("text") is { } t)
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(t);
                    }
                }
            }

            // OpenAI-shaped tool calls on the message; execution results in toolCallStates.
            var calls = new List<AgentToolCall>();
            var toolCalls = m.Property("toolCalls");
            if (toolCalls.IsArray)
            {
                foreach (var c in toolCalls.Items())
                {
                    var fn = c.Property("function");
                    calls.Add(new AgentToolCall(
                        fn.String("name") ?? "unknown", fn.String("arguments"), null, false, 0));
                }
            }
            var states = item.Property("toolCallStates");
            if (states.IsArray)
            {
                int at = 0;
                foreach (var st in states.Items())
                {
                    var output = st.Property("output");
                    string? result = output.IsValid ? output.RawText() : null;
                    bool isError = st.String("status") == "errored";
                    if (at < calls.Count)
                        calls[at] = calls[at] with { ResultText = result, IsError = isError };
                    at++;
                }
            }

            string? thinking = rawRole == "thinking"
                ? null
                : item.Property("reasoning").String("text");
            var u = m.Property("usage");
            AgentUsage? usage = u.IsObject
                ? new AgentUsage(
                    u.Int64("promptTokens"), u.Int64("completionTokens"),
                    u.Property("promptTokensDetails").Int64("cachedTokens"),
                    u.Property("promptTokensDetails").Int64("cacheWriteTokens"),
                    CostUsd: null)
                : null;

            if (text.Length == 0 && thinking is null && calls.Count == 0
                && rawRole != "thinking")
                continue;

            turns.Add(new AgentTurn(turns.Count, role, 0)
            {
                Text = rawRole == "thinking" ? null : (text.Length > 0 ? text.ToString() : null),
                Thinking = rawRole == "thinking" ? text.ToString() : thinking,
                Usage = usage is { IsEmpty: false } ? usage : null,
                ToolCalls = calls,
            });
        }

        if (turns.Count == 0) yield break;

        var sessionUsage = root.Property("usage");
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sessionUsage.Property("totalCost").AsDouble() is { } cost)
            meta["totalCost"] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (root.String("chatModelTitle") is { } modelTitle)
            meta["chatModelTitle"] = modelTitle;

        yield return new AgentSession(
            ProviderKey,
            AgentTraceEmitter.SanitizeKey(root.String("sessionId")
                ?? Path.GetFileNameWithoutExtension(filePath)),
            0, 0, turns)
        {
            Title = root.String("title"),
            Cwd = root.String("workspaceDirectory"),
            Meta = meta,
        };
    }
}
