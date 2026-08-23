using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// OpenCode (sst/opencode), both storage generations under ~/.local/share/opencode/:
/// the current SQLite era (`opencode.db`: session / message / part tables, message and
/// part payloads as JSON `data` columns) and the flat-JSON era
/// (`storage/session/&lt;projectID&gt;/&lt;ses_…&gt;.json` with `storage/message/&lt;sessionID&gt;/*.json`
/// and `storage/part/&lt;messageID&gt;/*.json`). Ids carry `ses_`/`msg_`/`prt_` prefixes.
/// Times are epoch ms; assistant messages carry modelID/providerID, cost, and token
/// counts; tool parts carry callID/tool/state{input,output,status}.
/// </summary>
public sealed class OpenCodeAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "opencode";

    public bool CanHandle(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if ((name == "opencode.db" || (name.StartsWith("opencode-", StringComparison.Ordinal)
                                       && name.EndsWith(".db", StringComparison.Ordinal)))
            && SqliteSniff.IsSqlite(filePath))
            return true;
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        if (!filePath.Contains($"storage{Path.DirectorySeparatorChar}session", StringComparison.Ordinal))
            return false;
        var head = AdapterJson.Head(filePath, 256);
        return head is not null && head.Contains("\"ses_", StringComparison.Ordinal);
    }

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".local", "share", "opencode");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filePath.EndsWith(".db", StringComparison.Ordinal))
        {
            foreach (var s in ParseDb(filePath, ct)) yield return s;
            yield break;
        }
        if (await ParseJsonEraAsync(filePath, ct) is { } session) yield return session;
    }

    // ── SQLite era ────────────────────────────────────────────────────────────────

    private IEnumerable<AgentSession> ParseDb(string dbPath, CancellationToken ct)
    {
        using var conn = SqliteSniff.OpenReadOnly(dbPath);
        if (conn is null) yield break;

        var sessions = new List<(string Id, string? Title, long Created, long Updated, string? ModelJson)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, time_created, time_updated, model FROM session";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sessions.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
        }

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var turns = new List<AgentTurn>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT m.id, m.data, m.time_created FROM message m "
                    + "WHERE m.session_id = $s ORDER BY m.time_created, m.id";
                cmd.Parameters.AddWithValue("$s", s.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string messageId = r.GetString(0);
                    string data = r.IsDBNull(1) ? "{}" : r.GetString(1);
                    long ts = AdapterJson.MsUs(r.IsDBNull(2) ? null : r.GetInt64(2));
                    var parts = ReadDbParts(conn, messageId);
                    if (BuildTurn(turns.Count, data, parts, ts) is { } turn) turns.Add(turn);
                }
            }
            if (turns.Count == 0) continue;
            yield return new AgentSession(
                ProviderKey, AgentTraceEmitter.SanitizeKey(s.Id),
                AdapterJson.MsUs(s.Created), AdapterJson.MsUs(s.Updated), turns)
            {
                Title = s.Title,
            };
        }
    }

    private static List<string> ReadDbParts(SqliteConnection conn, string messageId)
    {
        var parts = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM part WHERE message_id = $m ORDER BY id";
        cmd.Parameters.AddWithValue("$m", messageId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0)) parts.Add(r.GetString(0));
        return parts;
    }

    // ── flat-JSON era ─────────────────────────────────────────────────────────────

    private async Task<AgentSession?> ParseJsonEraAsync(string sessionPath, CancellationToken ct)
    {
        using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(sessionPath, ct));
        if (doc is null) return null;
        var info = doc.Root;
        string? sessionId = info.String("id");
        if (sessionId is null) return null;

        // storage/session/<projectID>/<ses>.json → storage/message/<ses>/*.json
        var storageRoot = new DirectoryInfo(sessionPath).Parent?.Parent?.Parent;
        if (storageRoot is null) return null;
        string messageDir = Path.Combine(storageRoot.FullName, "message", sessionId);
        if (!Directory.Exists(messageDir)) return null;

        var turns = new List<AgentTurn>();
        foreach (var messagePath in Directory.EnumerateFiles(messageDir, "*.json")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string data = await File.ReadAllTextAsync(messagePath, ct);
            string messageId = Path.GetFileNameWithoutExtension(messagePath);
            var parts = new List<string>();
            string partDir = Path.Combine(storageRoot.FullName, "part", messageId);
            if (Directory.Exists(partDir))
                foreach (var partPath in Directory.EnumerateFiles(partDir, "*.json")
                             .OrderBy(p => p, StringComparer.Ordinal))
                    parts.Add(await File.ReadAllTextAsync(partPath, ct));
            if (BuildTurn(turns.Count, data, parts, 0) is { } turn) turns.Add(turn);
        }
        if (turns.Count == 0) return null;

        var time = info.Property("time");
        return new AgentSession(
            ProviderKey, AgentTraceEmitter.SanitizeKey(sessionId),
            AdapterJson.MsUs(time.Int64("created")), AdapterJson.MsUs(time.Int64("updated")), turns)
        {
            Title = info.String("title"),
        };
    }

    // ── shared message/part shaping ───────────────────────────────────────────────

    private static AgentTurn? BuildTurn(int ordinal, string messageJson, List<string> partJsons, long fallbackTs)
    {
        using var doc = JsonAstDocument.TryParse(messageJson);
        if (doc is null) return null;
        var m = doc.Root;
        string role = AgentRoles.Normalize(m.String("role"));
        long ts = AdapterJson.MsUs(m.Property("time").Int64("created"));
        if (ts == 0) ts = fallbackTs;

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<AgentToolCall>();
        foreach (var partJson in partJsons)
        {
            using var partDoc = JsonAstDocument.TryParse(partJson);
            if (partDoc is null) continue;
            var p = partDoc.Root;
            switch (p.String("type"))
            {
                case "text":
                    if (text.Length > 0) text.Append('\n');
                    text.Append(p.String("text"));
                    break;
                case "reasoning":
                    if (thinking.Length > 0) thinking.Append('\n');
                    thinking.Append(p.String("text"));
                    break;
                case "tool":
                {
                    var state = p.Property("state");
                    var input = state.Property("input");
                    var output = state.Property("output");
                    calls.Add(new AgentToolCall(
                        p.String("tool") ?? "unknown",
                        input.IsValid ? input.RawText() : null,
                        output.Kind == JsonAstKind.String ? output.AsString()
                            : output.IsValid ? output.RawText() : null,
                        state.String("status") == "error",
                        AdapterJson.MsUs(state.Property("time").Int64("start"))));
                    break;
                }
            }
        }

        var tokens = m.Property("tokens");
        var cache = tokens.Property("cache");
        AgentUsage? usage = tokens.IsObject
            ? new AgentUsage(
                tokens.Int64("input"), tokens.Int64("output"),
                cache.Int64("read"), cache.Int64("write"),
                m.Property("cost").AsDouble())
            : null;

        if (text.Length == 0 && thinking.Length == 0 && calls.Count == 0) return null;
        return new AgentTurn(ordinal, role, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            Model = m.String("modelID"),
            Usage = usage is { IsEmpty: false } ? usage : null,
            ToolCalls = calls,
        };
    }
}

/// <summary>Shared SQLite sniff/open for the container-codec adapters.</summary>
internal static class SqliteSniff
{
    internal static bool IsSqlite(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[16];
            return fs.Read(magic) == 16
                && Encoding.ASCII.GetString(magic[..15]) == "SQLite format 3";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static SqliteConnection? OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try { conn.Open(); }
        catch (SqliteException) { conn.Dispose(); return null; }
        return conn;
    }

    internal static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() is not null;
    }
}
