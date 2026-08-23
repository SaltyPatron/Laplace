using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Cursor agent chats: ~/.cursor/chats/&lt;workspace-hash&gt;/&lt;agentId&gt;/store.db, a SQLite
/// store of content-addressed blobs (a THIRD-PARTY container this adapter unpacks —
/// not substrate SQL). Message blobs are plaintext JSON {"role": user|assistant|tool|
/// system, "content": string | [{type: text|tool-call|tool-result|reasoning, …}]};
/// other blobs (summaries, encrypted state) do not parse as role-tagged JSON and are
/// skipped. Blob rowid order approximates conversation order. The sibling meta.json
/// carries {title, createdAtMs, updatedAtMs}.
/// </summary>
public sealed class CursorAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "cursor";

    public bool CanHandle(string filePath)
    {
        if (Path.GetFileName(filePath) != "store.db") return false;
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(filePath)!, "meta.json"))) return false;
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

    public IEnumerable<string> DefaultRoots(string homeDir)
    {
        yield return Path.Combine(homeDir, ".cursor", "chats");
    }

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(filePath)!;
        string sessionKey = new DirectoryInfo(dir).Name;
        string? title = null;
        long startUs = 0, endUs = 0;

        string metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath)
            && JsonAstDocument.TryParse(await File.ReadAllBytesAsync(metaPath, ct)) is { } metaDoc)
        {
            using (metaDoc)
            {
                title = metaDoc.Root.String("title");
                startUs = AdapterJson.MsUs(metaDoc.Root.Int64("createdAtMs"));
                endUs = AdapterJson.MsUs(metaDoc.Root.Int64("updatedAtMs"));
            }
        }

        var turns = new List<AgentTurn>();
        var callsById = new Dictionary<string, (int TurnAt, AgentToolCall Call)>(StringComparer.Ordinal);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM blobs ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) continue;
                byte[] data = (byte[])reader[0];
                if (data.Length < 2 || data[0] != (byte)'{') continue;
                using var doc = JsonAstDocument.TryParse(data);
                if (doc is null) continue;
                var msg = doc.Root;
                if (msg.String("role") is not { } rawRole) continue;
                AppendMessage(msg, rawRole, turns, callsById, startUs);
            }
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(ProviderKey, sessionKey, startUs, endUs, turns)
        {
            Title = title,
        };
    }

    private static void AppendMessage(
        in JsonAstCursor msg,
        string rawRole,
        List<AgentTurn> turns,
        Dictionary<string, (int TurnAt, AgentToolCall Call)> callsById,
        long ts)
    {
        string role = AgentRoles.Normalize(rawRole);
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<AgentToolCall>();
        bool resultOnly = true;

        var content = msg.Property("content");
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
                    case "reasoning" or "thinking":
                        if (thinking.Length > 0) thinking.Append('\n');
                        thinking.Append(block.String("text") ?? block.String("reasoning"));
                        resultOnly = false;
                        break;
                    case "tool-call":
                    {
                        string name = block.String("toolName") ?? "unknown";
                        var args = block.Property("args");
                        var call = new AgentToolCall(
                            name, args.IsValid ? args.RawText() : null, null, false, ts);
                        calls.Add(call);
                        resultOnly = false;
                        if (block.String("toolCallId") is { } cid)
                            callsById[cid] = (turns.Count, call);
                        break;
                    }
                    case "tool-result":
                    {
                        string? cid = block.String("toolCallId");
                        var r = block.Property("result");
                        string? result = r.Kind == JsonAstKind.String
                            ? r.AsString()
                            : r.IsValid ? r.RawText() : null;
                        if (cid is not null && callsById.TryGetValue(cid, out var entry))
                        {
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
                        }
                        else if (result is not null)
                        {
                            string name = block.String("toolName") ?? "unknown";
                            calls.Add(new AgentToolCall(name, null, result, false, ts));
                            resultOnly = false;
                        }
                        break;
                    }
                }
            }
        }

        // A pure result-plumbing message was already joined onto its owning call.
        if (resultOnly && text.Length == 0 && thinking.Length == 0 && calls.Count == 0)
            return;

        turns.Add(new AgentTurn(turns.Count, role, ts)
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            ToolCalls = calls,
        });
    }
}
