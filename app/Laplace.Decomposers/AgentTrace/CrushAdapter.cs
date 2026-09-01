using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Charm Crush: per-project &lt;cwd&gt;/.crush/crush.db (SQLite) — sessions {id, title,
/// prompt_tokens, completion_tokens, cost, created_at (epoch s)}, messages {id,
/// session_id, role, parts (JSON [{type, data}]), model, provider, created_at}.
/// Part types: text / reasoning / tool_call{id,name,input} / tool_result
/// {tool_call_id, content, is_error} / finish{reason} / shell_command / image_url /
/// binary.
/// </summary>
public sealed class CrushAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "crush";

    public bool CanHandle(string filePath) =>
        Path.GetFileName(filePath) == "crush.db"
        && filePath.Contains($"{Path.DirectorySeparatorChar}.crush{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
        && SqliteSniff.IsSqlite(filePath);

    /// <summary>Per-project databases — no home root; explicit paths only.</summary>
    public IEnumerable<string> DefaultRoots(string homeDir) => [];

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        using var conn = SqliteSniff.OpenReadOnly(filePath);
        if (conn is null || !SqliteSniff.HasTable(conn, "sessions")
            || !SqliteSniff.HasTable(conn, "messages"))
            yield break;

        // One ordered join replaces the old query-per-session loop. This is a foreign
        // SQLite codec, but it is still on the ingest critical path: thousands of
        // project sessions must not mean thousands of commands before compose begins.
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT s.id, s.title, s.created_at, s.updated_at, s.cost, "
            + "s.prompt_tokens, s.completion_tokens, "
            + "m.role, m.parts, m.model, m.created_at "
            + "FROM sessions s LEFT JOIN messages m ON m.session_id = s.id "
            + "ORDER BY s.id, m.created_at, m.id";
        using var r = cmd.ExecuteReader();

        string? sessionId = null, title = null;
        long sessionCreated = 0, sessionUpdated = 0, promptTokens = 0, completionTokens = 0;
        double cost = 0;
        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);

        AgentSession? FlushSession()
        {
            if (sessionId is null || turns.Count == 0) return null;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (cost > 0)
                meta["totalCost"] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (promptTokens > 0) meta["prompt_tokens"] = promptTokens.ToString();
            if (completionTokens > 0) meta["completion_tokens"] = completionTokens.ToString();
            var result = new AgentSession(
                ProviderKey, AgentTraceEmitter.SanitizeKey(sessionId),
                sessionCreated > 10_000_000_000L ? sessionCreated * 1000 : sessionCreated * 1_000_000,
                sessionUpdated > 10_000_000_000L ? sessionUpdated * 1000 : sessionUpdated * 1_000_000,
                turns)
            {
                Title = title,
                Meta = meta,
            };
            turns = new List<AgentTurn>();
            callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
            return result;
        }

        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            string rowSessionId = r.GetString(0);
            if (!StringComparer.Ordinal.Equals(sessionId, rowSessionId))
            {
                if (FlushSession() is { } session) yield return session;
                sessionId = rowSessionId;
                title = r.IsDBNull(1) ? null : r.GetString(1);
                sessionCreated = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                sessionUpdated = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                cost = r.IsDBNull(4) ? 0 : r.GetDouble(4);
                promptTokens = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                completionTokens = r.IsDBNull(6) ? 0 : r.GetInt64(6);
            }

            if (r.IsDBNull(7)) continue;
            string role = AgentRoles.Normalize(r.GetString(7));
            long created = r.IsDBNull(10) ? 0 : r.GetInt64(10);
            long ts = created > 10_000_000_000L ? created * 1000 : created * 1_000_000;
            string? model = r.IsDBNull(9) ? null : r.GetString(9);
            if (BuildTurn(turns, callsById, role, r.IsDBNull(8) ? "[]" : r.GetString(8),
                    model, ts) is { } turn)
                turns.Add(turn);
        }

        if (FlushSession() is { } finalSession) yield return finalSession;
    }

    private static AgentTurn? BuildTurn(
        List<AgentTurn> turns,
        Dictionary<string, (int TurnAt, AgentToolCall Call)> callsById,
        string role, string partsJson, string? model, long ts)
    {
        using var doc = JsonAstDocument.TryParse(partsJson);
        if (doc is null || !doc.Root.IsArray) return null;

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<AgentToolCall>();
        string? stopReason = null;
        bool resultOnly = true;

        foreach (var part in doc.Root.Items())
        {
            var data = part.Property("data");
            switch (part.String("type"))
            {
                case "text":
                    if (text.Length > 0) text.Append('\n');
                    text.Append(data.String("text"));
                    resultOnly = false;
                    break;
                case "reasoning":
                    if (thinking.Length > 0) thinking.Append('\n');
                    thinking.Append(data.String("thinking"));
                    resultOnly = false;
                    break;
                case "tool_call":
                {
                    var call = new AgentToolCall(
                        data.String("name") ?? "unknown", data.String("input"), null, false, ts);
                    calls.Add(call);
                    resultOnly = false;
                    if (data.String("id") is { } cid) callsById[cid] = (turns.Count, call);
                    break;
                }
                case "tool_result":
                {
                    if (data.String("tool_call_id") is not { } cid
                        || !callsById.TryGetValue(cid, out var entry))
                        break;
                    var filled = entry.Call with
                    {
                        ResultText = data.String("content"),
                        IsError = data.Bool("is_error") == true,
                    };
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
                case "shell_command":
                {
                    calls.Add(new AgentToolCall(
                        "shell", data.String("command"), data.String("output"),
                        (data.Int64("exit_code") ?? 0) != 0, ts));
                    resultOnly = false;
                    break;
                }
                case "finish":
                    stopReason = data.String("reason");
                    break;
            }
        }

        if (resultOnly && text.Length == 0 && thinking.Length == 0 && calls.Count == 0)
            return null;
        return new AgentTurn(turns.Count, role, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            Model = model,
            StopReason = stopReason,
            ToolCalls = calls,
        };
    }
}
