using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Goose (Block) sessions under ~/.local/share/goose/sessions/: the legacy JSONL era
/// (`YYYYMMDD_HHMMSS.jsonl`: line 1 = snake_case SessionMetadata {working_dir,
/// description, …token counters}; lines 2+ = camelCase Message {id, role, created
/// (epoch SECONDS), content[{type: text|thinking|toolRequest|toolResponse|…}]}) and
/// the current SQLite era (`sessions.db`: sessions / messages / usage_ledger —
/// per-inference model + tokens + cost). Both generations coexist on disk.
/// </summary>
public sealed class GooseAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "goose";

    public bool CanHandle(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (name == "sessions.db" && filePath.Contains("goose", StringComparison.OrdinalIgnoreCase)
            && SqliteSniff.IsSqlite(filePath))
            return true;
        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}_\d{6}\.jsonl$")) return false;
        var line = AdapterJson.FirstLine(filePath);
        return line is not null && line.Contains("\"working_dir\"", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".local", "share", "goose", "sessions");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filePath.EndsWith(".db", StringComparison.Ordinal))
        {
            foreach (var s in ParseDb(filePath, ct)) yield return s;
            yield break;
        }

        var turns = new List<AgentTurn>();
        string? cwd = null, title = null;
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        long firstUs = 0, lastUs = 0;
        bool first = true;

        await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
        {
            using var _ = doc;
            var root = doc.Root;
            if (first)
            {
                first = false;
                cwd = root.String("working_dir");
                title = root.String("description");
                AdapterJson.CollectMeta(root, meta, "working_dir", "description");
                continue;
            }
            if (BuildTurn(turns, root) is { } turn)
            {
                if (turn.TimestampUnixUs > 0)
                {
                    if (firstUs == 0) firstUs = turn.TimestampUnixUs;
                    lastUs = turn.TimestampUnixUs;
                }
                turns.Add(turn);
            }
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(
            ProviderKey,
            AgentTraceEmitter.SanitizeKey(Path.GetFileNameWithoutExtension(filePath)),
            firstUs, lastUs, turns)
        {
            Cwd = cwd,
            Title = title,
            Meta = meta,
        };
    }

    /// <summary>One legacy message line → turn (toolRequest/toolResponse joined by id).</summary>
    private static AgentTurn? BuildTurn(List<AgentTurn> turns, in JsonAstCursor m)
    {
        string role = AgentRoles.Normalize(m.String("role"));
        long ts = (m.Int64("created") ?? 0) * 1_000_000;
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<AgentToolCall>();
        bool responseOnly = true;

        var content = m.Property("content");
        if (!content.IsArray) return null;
        foreach (var block in content.Items())
        {
            switch (block.String("type"))
            {
                case "text":
                    if (text.Length > 0) text.Append('\n');
                    text.Append(block.String("text"));
                    responseOnly = false;
                    break;
                case "thinking":
                    if (thinking.Length > 0) thinking.Append('\n');
                    thinking.Append(block.String("thinking"));
                    responseOnly = false;
                    break;
                case "toolRequest":
                {
                    var value = block.Property("toolCall").Property("value");
                    var args = value.Property("arguments");
                    calls.Add(new AgentToolCall(
                        value.String("name") ?? "unknown",
                        args.IsValid ? args.RawText() : null, null, false, ts));
                    responseOnly = false;
                    break;
                }
                case "toolResponse":
                {
                    // Joined onto the LAST pending request (goose interleaves per turn).
                    var result = block.Property("toolResult");
                    string? output = result.IsValid ? result.RawText() : null;
                    bool isError = result.String("status") == "error";
                    if (calls.Count > 0 && calls[^1].ResultText is null)
                        calls[^1] = calls[^1] with { ResultText = output, IsError = isError };
                    else if (FillLastPending(turns, output, isError)) { }
                    break;
                }
            }
        }

        if (responseOnly && text.Length == 0 && thinking.Length == 0 && calls.Count == 0)
            return null;
        return new AgentTurn(turns.Count, role, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            ToolCalls = calls,
        };
    }

    private static bool FillLastPending(List<AgentTurn> turns, string? output, bool isError)
    {
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            var list = turns[i].ToolCalls;
            for (int j = list.Count - 1; j >= 0; j--)
            {
                if (list[j].ResultText is not null) continue;
                var copy = new List<AgentToolCall>(list);
                copy[j] = copy[j] with { ResultText = output, IsError = isError };
                turns[i] = turns[i] with { ToolCalls = copy };
                return true;
            }
        }
        return false;
    }

    private IEnumerable<AgentSession> ParseDb(string dbPath, CancellationToken ct)
    {
        using var conn = SqliteSniff.OpenReadOnly(dbPath);
        if (conn is null || !SqliteSniff.HasTable(conn, "sessions")
            || !SqliteSniff.HasTable(conn, "messages"))
            yield break;

        var sessions = new List<(string Id, string? Desc, string? Cwd, string? Model)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, description, working_dir, model_config_json FROM sessions";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sessions.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            string? model = null;
            if (s.Model is not null)
            {
                using var modelDoc = JsonAstDocument.TryParse(s.Model);
                model = modelDoc?.Root.String("model");
            }

            var turns = new List<AgentTurn>();
            long firstUs = 0, lastUs = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT role, content_json, created_timestamp FROM messages "
                    + "WHERE session_id = $s ORDER BY created_timestamp, id";
                cmd.Parameters.AddWithValue("$s", s.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long raw = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    long ts = raw > 10_000_000_000L ? raw * 1000 : raw * 1_000_000;
                    string wrapper = $"{{\"role\":\"{r.GetString(0)}\",\"created\":0,\"content\":"
                                     + (r.IsDBNull(1) ? "[]" : r.GetString(1)) + "}";
                    using var doc = JsonAstDocument.TryParse(wrapper);
                    if (doc is null) continue;
                    if (BuildTurn(turns, doc.Root) is { } turn)
                    {
                        turn = turn with { TimestampUnixUs = ts, Model = model };
                        if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }
                        turns.Add(turn);
                    }
                }
            }
            if (turns.Count == 0) continue;
            yield return new AgentSession(
                ProviderKey, AgentTraceEmitter.SanitizeKey(s.Id), firstUs, lastUs, turns)
            {
                Title = s.Desc,
                Cwd = s.Cwd,
            };
        }
    }
}
