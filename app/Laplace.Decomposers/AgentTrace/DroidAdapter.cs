using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Factory Droid CLI: ~/.factory/sessions/&lt;workspace-slug&gt;/&lt;uuid&gt;.jsonl — line 1
/// `{"type":"session_start", id, title, cwd, version, …}`, then `{"type":"message",
/// id, timestamp (ISO), message:{role, content: Anthropic-style blocks}}`. The
/// sibling `&lt;uuid&gt;.settings.json` sidecar carries the model and aggregate
/// tokenUsage {inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens}.
/// </summary>
public sealed class DroidAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "droid";

    public bool CanHandle(string filePath)
    {
        if (!filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var line = AdapterJson.FirstLine(filePath);
        return line is not null
            && line.Contains("\"session_start\"", StringComparison.Ordinal)
            && (line.Contains("\"sessionTitleAutoStage\"", StringComparison.Ordinal)
                || line.Contains("\"isSessionTitleManuallySet\"", StringComparison.Ordinal)
                || filePath.Contains($"{Path.DirectorySeparatorChar}.factory{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".factory", "sessions");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
        var sessionMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionKey = null, title = null, cwd = null;
        long firstUs = 0, lastUs = 0;

        // Sidecar: model + aggregate usage.
        string? model = null;
        AgentUsage? totals = null;
        string sidecar = Path.ChangeExtension(filePath, null) + ".settings.json";
        if (File.Exists(sidecar)
            && JsonAstDocument.TryParse(await File.ReadAllBytesAsync(sidecar, ct)) is { } sideDoc)
        {
            using (sideDoc)
            {
                model = sideDoc.Root.String("model");
                var u = sideDoc.Root.Property("tokenUsage");
                if (u.IsObject)
                {
                    totals = new AgentUsage(
                        u.Int64("inputTokens"), u.Int64("outputTokens"),
                        u.Int64("cacheReadTokens"), u.Int64("cacheCreationTokens"),
                        CostUsd: null);
                }
                AdapterJson.CollectMeta(sideDoc.Root, sessionMeta, "tokenUsage", "model");
            }
        }

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var root = doc.Root;
            switch (root.String("type"))
            {
                case "session_start":
                    sessionKey = root.String("id");
                    title = root.String("title") ?? root.String("sessionTitle");
                    cwd = root.String("cwd");
                    if (root.String("version") is { } v) sessionMeta["version"] = v;
                    break;

                case "message":
                {
                    var m = root.Property("message");
                    if (!m.IsObject) break;
                    long ts = AdapterJson.IsoUs(root.String("timestamp"));
                    if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }
                    AppendMessage(turns, callsById, m, model, ts);
                    break;
                }
            }
        }

        if (turns.Count == 0) yield break;
        // Aggregate usage attaches at session grain (the sidecar is session-total).
        if (totals is { IsEmpty: false })
        {
            // Session totals emit from turn sums; put the sidecar totals on the LAST
            // assistant turn only if no turn carries usage (droid has no per-turn usage).
            int lastAssistant = turns.FindLastIndex(t => t.Role == AgentRoles.Assistant);
            if (lastAssistant >= 0 && turns.All(t => t.Usage is null))
                turns[lastAssistant] = turns[lastAssistant] with { Usage = totals };
        }
        yield return new AgentSession(
            ProviderKey,
            AgentTraceEmitter.SanitizeKey(sessionKey ?? Path.GetFileNameWithoutExtension(filePath)),
            firstUs, lastUs, turns)
        {
            Title = title,
            Cwd = cwd,
            Meta = sessionMeta,
        };
    }

    private static void AppendMessage(
        List<AgentTurn> turns,
        Dictionary<string, (int TurnAt, AgentToolCall Call)> callsById,
        in JsonAstCursor m, string? model, long ts)
    {
        string role = AgentRoles.Normalize(m.String("role"));
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
                        if (block.String("tool_use_id") is not { } cid
                            || !callsById.TryGetValue(cid, out var entry))
                            break;
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
            return;
        turns.Add(new AgentTurn(turns.Count, role, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            Model = role == AgentRoles.Assistant ? model : null,
            ToolCalls = calls,
        });
    }
}
