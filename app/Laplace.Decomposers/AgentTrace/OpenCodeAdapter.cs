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

        // One ordered join streams the complete container. The old shape issued one
        // message query per session and then one part query per message (and attempted
        // those part queries while the message reader was still open). Large OpenCode
        // histories therefore paid O(sessions + messages) SQLite commands before any
        // substrate work began. Group the joined rows here; SQLite owns the scan/order
        // once and the codec only reconstructs its nested JSON records.
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT s.id, s.title, s.time_created, s.time_updated, "
            + "m.id, m.data, m.time_created, p.data "
            + "FROM session s "
            + "LEFT JOIN message m ON m.session_id = s.id "
            + "LEFT JOIN part p ON p.message_id = m.id "
            + "ORDER BY s.id, m.time_created, m.id, p.id";
        using var r = cmd.ExecuteReader();

        string? sessionId = null, title = null, messageId = null, messageJson = null;
        long sessionCreated = 0, sessionUpdated = 0, messageTs = 0;
        var turns = new List<AgentTurn>();
        var parts = new List<string>();

        void FlushMessage()
        {
            if (messageId is not null
                && BuildTurn(turns.Count, messageJson ?? "{}", parts, messageTs) is { } turn)
                turns.Add(turn);
            messageId = null;
            messageJson = null;
            messageTs = 0;
            parts.Clear();
        }

        AgentSession? FlushSession()
        {
            FlushMessage();
            if (sessionId is null || turns.Count == 0) return null;
            var result = new AgentSession(
                ProviderKey, AgentTraceEmitter.SanitizeKey(sessionId),
                AdapterJson.MsUs(sessionCreated), AdapterJson.MsUs(sessionUpdated), turns)
            {
                Title = title,
            };
            turns = new List<AgentTurn>();
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
            }

            string? rowMessageId = r.IsDBNull(4) ? null : r.GetString(4);
            if (!StringComparer.Ordinal.Equals(messageId, rowMessageId))
            {
                FlushMessage();
                messageId = rowMessageId;
                messageJson = r.IsDBNull(5) ? "{}" : r.GetString(5);
                messageTs = AdapterJson.MsUs(r.IsDBNull(6) ? null : r.GetInt64(6));
            }
            if (!r.IsDBNull(7)) parts.Add(r.GetString(7));
        }

        if (FlushSession() is { } finalSession) yield return finalSession;
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
