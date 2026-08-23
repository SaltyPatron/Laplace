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

        var sessions = new List<(string Id, string? Title, long Created, long Updated, double Cost,
            long PromptTokens, long CompletionTokens)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, created_at, updated_at, cost, "
                + "prompt_tokens, completion_tokens FROM sessions";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sessions.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    r.IsDBNull(4) ? 0 : r.GetDouble(4),
                    r.IsDBNull(5) ? 0 : r.GetInt64(5), r.IsDBNull(6) ? 0 : r.GetInt64(6)));
        }

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var turns = new List<AgentTurn>();
            var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT role, parts, model, created_at FROM messages "
                    + "WHERE session_id = $s ORDER BY created_at, id";
                cmd.Parameters.AddWithValue("$s", s.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string role = AgentRoles.Normalize(r.GetString(0));
                    long created = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                    long ts = created > 10_000_000_000L ? created * 1000 : created * 1_000_000;
                    string? model = r.IsDBNull(2) ? null : r.GetString(2);
                    if (BuildTurn(turns, callsById, role, r.IsDBNull(1) ? "[]" : r.GetString(1),
                            model, ts) is { } turn)
                        turns.Add(turn);
                }
            }
            if (turns.Count == 0) continue;

            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (s.Cost > 0)
                meta["totalCost"] = s.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (s.PromptTokens > 0) meta["prompt_tokens"] = s.PromptTokens.ToString();
            if (s.CompletionTokens > 0) meta["completion_tokens"] = s.CompletionTokens.ToString();

            yield return new AgentSession(
                ProviderKey, AgentTraceEmitter.SanitizeKey(s.Id),
                s.Created > 10_000_000_000L ? s.Created * 1000 : s.Created * 1_000_000,
                s.Updated > 10_000_000_000L ? s.Updated * 1000 : s.Updated * 1_000_000,
                turns)
            {
                Title = s.Title,
                Meta = meta,
            };
        }
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
