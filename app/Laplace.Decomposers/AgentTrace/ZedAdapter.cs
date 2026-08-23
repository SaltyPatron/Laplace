using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Zed agent threads: ~/.local/share/zed/threads/threads.db — SQLite `threads`
/// {id, summary, updated_at, data_type ("zstd"|"json"), data BLOB}. The blob is a
/// DbThread JSON (version "0.3.0": externally-tagged messages {"User":{content:[
/// {"Text":…}|…]}} / {"Agent":{content:[{"Text"|"Thinking"|"ToolUse"}…],
/// tool_results:{id:{…}}}}, cumulative_token_usage, model{provider,model}) or the
/// legacy "0.2.0" SerializedThread {messages:[{role, segments, tool_uses,
/// tool_results}]}. zstd blobs decode via the managed ZstdSharp decompressor.
/// </summary>
public sealed class ZedAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "zed";

    public bool CanHandle(string filePath) =>
        Path.GetFileName(filePath) == "threads.db"
        && filePath.Contains($"zed{Path.DirectorySeparatorChar}threads", StringComparison.Ordinal)
        && SqliteSniff.IsSqlite(filePath);

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".local", "share", "zed", "threads");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        using var conn = SqliteSniff.OpenReadOnly(filePath);
        if (conn is null || !SqliteSniff.HasTable(conn, "threads")) yield break;

        var rows = new List<(string Id, string? Summary, string? UpdatedAt, string? DataType, byte[] Data)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, summary, updated_at, data_type, data FROM threads";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(4)) continue;
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    (byte[])r[4]));
            }
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            byte[] json;
            try
            {
                json = row.DataType == "zstd"
                    ? new ZstdSharp.Decompressor().Unwrap(row.Data).ToArray()
                    : row.Data;
            }
            catch (ZstdSharp.ZstdException) { continue; }

            if (Parse(row.Id, row.Summary, row.UpdatedAt, json) is { } session)
                yield return session;
        }
    }

    private AgentSession? Parse(string threadId, string? summary, string? updatedAt, byte[] json)
    {
        using var doc = JsonAstDocument.TryParse(json);
        if (doc is null) return null;
        var root = doc.Root;
        var messages = root.Property("messages");
        if (!messages.IsArray) return null;

        long updatedUs = AdapterJson.IsoUs(root.String("updated_at") ?? updatedAt);
        var model = root.Property("model");
        string? modelId = model.String("model");

        var turns = new List<AgentTurn>();
        foreach (var m in messages.Items())
        {
            // Current era: externally tagged {"User":{...}} / {"Agent":{...}}.
            var user = m.Property("User");
            var agent = m.Property("Agent");
            if (user.IsObject) AppendCurrentUser(turns, user, updatedUs);
            else if (agent.IsObject) AppendCurrentAgent(turns, agent, modelId, updatedUs);
            else if (m.String("role") is { } role) AppendLegacy(turns, m, role, updatedUs);
        }
        if (turns.Count == 0) return null;

        var usage = root.Property("cumulative_token_usage");
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in (string[])["input_tokens", "output_tokens",
                     "cache_read_input_tokens", "cache_creation_input_tokens"])
            if (usage.Int64(key) is { } v and > 0)
                meta[key] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new AgentSession(
            ProviderKey, AgentTraceEmitter.SanitizeKey(threadId), 0, updatedUs, turns)
        {
            Title = root.String("title") ?? summary,
            Meta = meta,
        };
    }

    private static void AppendCurrentUser(List<AgentTurn> turns, in JsonAstCursor user, long ts)
    {
        var text = new StringBuilder();
        var content = user.Property("content");
        if (content.IsArray)
        {
            foreach (var block in content.Items())
            {
                string? t = block.Kind == JsonAstKind.String
                    ? block.AsString()
                    : block.String("Text") ?? block.Property("Text").AsString();
                if (string.IsNullOrEmpty(t)) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(t);
            }
        }
        if (text.Length == 0) return;
        turns.Add(new AgentTurn(turns.Count, AgentRoles.User, ts) { Text = text.ToString() });
    }

    private static void AppendCurrentAgent(
        List<AgentTurn> turns, in JsonAstCursor agent, string? modelId, long ts)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<AgentToolCall>();
        var resultsByUse = new Dictionary<string, (string? Content, bool IsError)>(StringComparer.Ordinal);

        foreach (var (useId, result) in agent.Property("tool_results").Pairs())
            resultsByUse[useId] = (
                result.Property("content") is { IsValid: true } rc
                    ? (rc.Kind == JsonAstKind.String ? rc.AsString() : rc.RawText())
                    : null,
                result.Bool("is_error") == true);

        var content = agent.Property("content");
        if (content.IsArray)
        {
            foreach (var block in content.Items())
            {
                if ((block.String("Text") ?? block.Property("Text").AsString()) is { } t)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(t);
                }
                else if (block.Property("Thinking") is { IsObject: true } th)
                {
                    if (thinking.Length > 0) thinking.Append('\n');
                    thinking.Append(th.String("text"));
                }
                else if (block.Property("ToolUse") is { IsObject: true } tu)
                {
                    var input = tu.Property("input");
                    string? useId = tu.String("id");
                    var (result, isError) = useId is not null
                        && resultsByUse.TryGetValue(useId, out var r) ? r : (null, false);
                    calls.Add(new AgentToolCall(
                        tu.String("name") ?? "unknown",
                        input.IsValid ? input.RawText() : null, result, isError, ts));
                }
            }
        }

        if (text.Length == 0 && thinking.Length == 0 && calls.Count == 0) return;
        turns.Add(new AgentTurn(turns.Count, AgentRoles.Assistant, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            Model = modelId,
            ToolCalls = calls,
        });
    }

    private static void AppendLegacy(List<AgentTurn> turns, in JsonAstCursor m, string role, long ts)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var segments = m.Property("segments");
        if (segments.IsArray)
        {
            foreach (var seg in segments.Items())
            {
                switch (seg.String("type"))
                {
                    case "text":
                        if (text.Length > 0) text.Append('\n');
                        text.Append(seg.String("text"));
                        break;
                    case "thinking":
                        if (thinking.Length > 0) thinking.Append('\n');
                        thinking.Append(seg.String("text"));
                        break;
                }
            }
        }

        var calls = new List<AgentToolCall>();
        var resultsById = new Dictionary<string, (string? Content, bool IsError)>(StringComparer.Ordinal);
        var toolResults = m.Property("tool_results");
        if (toolResults.IsArray)
        {
            foreach (var r in toolResults.Items())
                if (r.String("tool_use_id") is { } id)
                    resultsById[id] = (
                        r.Property("content") is { IsValid: true } rc
                            ? (rc.Kind == JsonAstKind.String ? rc.AsString() : rc.RawText())
                            : null,
                        r.Bool("is_error") == true);
        }
        var toolUses = m.Property("tool_uses");
        if (toolUses.IsArray)
        {
            foreach (var u in toolUses.Items())
            {
                var input = u.Property("input");
                var (result, isError) = u.String("id") is { } id
                    && resultsById.TryGetValue(id, out var r) ? r : (null, false);
                calls.Add(new AgentToolCall(
                    u.String("name") ?? "unknown",
                    input.IsValid ? input.RawText() : null, result, isError, ts));
            }
        }

        if (text.Length == 0 && thinking.Length == 0 && calls.Count == 0) return;
        turns.Add(new AgentTurn(turns.Count, AgentRoles.Normalize(role), ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            ToolCalls = calls,
        });
    }
}
