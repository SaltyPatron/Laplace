using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// GitHub Copilot CLI session state: ~/.copilot/session-state/&lt;sessionId&gt;.jsonl.
/// Envelope {type, data, id, timestamp, parentId}: session.start (identity/version),
/// session.info (gh login), user.message / assistant.message (content + toolRequests),
/// tool.execution_start {toolCallId, toolName, arguments} joined by
/// tool.execution_complete {toolCallId, success, result}; session.truncation and abort
/// land in metadata.
/// </summary>
public sealed partial class CopilotAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "copilot";

    [GeneratedRegex(@"as user:\s*(\S+)")]
    private static partial Regex GhUserPattern();

    public bool CanHandle(string filePath)
    {
        if (!filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (filePath.Contains("session-state", StringComparison.Ordinal)
            && filePath.Contains(".copilot", StringComparison.Ordinal))
            return true;
        var line = AdapterJson.FirstLine(filePath);
        return line is not null
            && line.Contains("\"session.start\"", StringComparison.Ordinal)
            && line.Contains("copilot", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".copilot", "session-state");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
        var sessionMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionKey = null, userKey = null;
        long firstUs = 0, lastUs = 0;
        int truncations = 0, aborts = 0, errors = 0;
        int lastAssistantAt = -1;

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var root = doc.Root;
            long ts = AdapterJson.IsoUs(root.String("timestamp"));
            if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }
            var data = root.Property("data");

            switch (root.String("type"))
            {
                case "session.start":
                    if (data.IsObject)
                    {
                        sessionKey = data.String("sessionId");
                        AdapterJson.CollectMeta(data, sessionMeta, "sessionId", "startTime");
                    }
                    break;

                case "session.info":
                    if (data.String("message") is { } msg
                        && GhUserPattern().Match(msg) is { Success: true } m)
                        userKey = m.Groups[1].Value;
                    break;

                case "user.message":
                    if (data.IsObject)
                        turns.Add(new AgentTurn(turns.Count, AgentRoles.User, ts)
                        {
                            Text = data.String("content"),
                        });
                    break;

                case "assistant.message":
                    if (data.IsObject)
                    {
                        lastAssistantAt = turns.Count;
                        turns.Add(new AgentTurn(turns.Count, AgentRoles.Assistant, ts)
                        {
                            Text = data.String("content"),
                        });
                    }
                    break;

                case "tool.execution_start":
                    if (data.String("toolCallId") is { } cid)
                    {
                        string name = data.String("toolName") ?? "unknown";
                        var args = data.Property("arguments");
                        string? input = args.Kind == JsonAstKind.String
                            ? args.AsString()
                            : args.IsValid ? args.RawText() : null;
                        var call = new AgentToolCall(name, input, null, false, ts);
                        int owner = lastAssistantAt >= 0 ? lastAssistantAt : EnsureToolTurn(turns, ts);
                        var t = turns[owner];
                        var list = new List<AgentToolCall>(t.ToolCalls) { call };
                        turns[owner] = t with { ToolCalls = list };
                        callsById[cid] = (owner, call);
                    }
                    break;

                case "tool.execution_complete":
                    if (data.String("toolCallId") is { } rid
                        && callsById.TryGetValue(rid, out var entry))
                    {
                        var r = data.Property("result");
                        string? result = r.Kind == JsonAstKind.String
                            ? r.AsString()
                            : r.IsValid ? r.RawText() : null;
                        bool ok = data.Bool("success") != false;
                        var filled = entry.Call with { ResultText = result, IsError = !ok };
                        var owner = turns[entry.TurnAt];
                        var list = new List<AgentToolCall>(owner.ToolCalls);
                        int at = list.IndexOf(entry.Call);
                        if (at >= 0)
                        {
                            list[at] = filled;
                            turns[entry.TurnAt] = owner with { ToolCalls = list };
                            callsById[rid] = (entry.TurnAt, filled);
                        }
                    }
                    break;

                case "session.truncation": truncations++; break;
                case "abort": aborts++; break;
                case "session.error": errors++; break;
            }
        }

        if (turns.Count == 0) yield break;
        if (truncations > 0) sessionMeta["truncations"] = truncations.ToString();
        if (aborts > 0) sessionMeta["aborts"] = aborts.ToString();
        if (errors > 0) sessionMeta["errors"] = errors.ToString();
        sessionKey ??= Path.GetFileNameWithoutExtension(filePath);
        yield return new AgentSession(ProviderKey, sessionKey, firstUs, lastUs, turns)
        {
            UserKey = userKey,
            Meta = sessionMeta,
        };
    }

    private static int EnsureToolTurn(List<AgentTurn> turns, long ts)
    {
        turns.Add(new AgentTurn(turns.Count, AgentRoles.Tool, ts));
        return turns.Count - 1;
    }
}
