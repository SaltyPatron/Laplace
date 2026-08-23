using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Last-resort adapter so no role-shaped log is ever omitted: any .json/.jsonl whose
/// records carry role+content (directly, under "message", or as a document with a
/// "messages"/"history" array — the OpenAI chat-export family). Runs LAST in the
/// registry; a file a specific adapter claims never reaches it.
/// </summary>
public sealed class GenericJsonAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "generic";

    public bool CanHandle(string filePath) =>
        filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Never in home discovery — generic shapes only ingest via explicit paths.</summary>
    public IEnumerable<string> DefaultRoots(string homeDir) => [];

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        var turns = new List<AgentTurn>();
        long firstUs = 0, lastUs = 0;

        if (filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var doc in AdapterJson.ReadJsonlAsync(filePath, ct))
            {
                using var _ = doc;
                Accept(doc.Root, turns, ref firstUs, ref lastUs);
            }
        }
        else
        {
            using var doc = JsonAstDocument.TryParse(await File.ReadAllBytesAsync(filePath, ct));
            if (doc is null) yield break;
            var root = doc.Root;
            if (root.IsArray)
            {
                foreach (var e in root.Items())
                    Accept(e, turns, ref firstUs, ref lastUs);
            }
            else if (root.IsObject)
            {
                foreach (var key in (string[])["messages", "history", "turns", "conversation"])
                {
                    var arr = root.Property(key);
                    if (!arr.IsArray) continue;
                    foreach (var e in arr.Items())
                        Accept(e, turns, ref firstUs, ref lastUs);
                    break;
                }
            }
        }

        if (turns.Count == 0) yield break;
        yield return new AgentSession(
            ProviderKey,
            SessionKey: Path.GetFileNameWithoutExtension(filePath),
            StartedAtUnixUs: firstUs,
            EndedAtUnixUs: lastUs,
            Turns: turns);
    }

    private static void Accept(
        in JsonAstCursor record, List<AgentTurn> turns, ref long firstUs, ref long lastUs)
    {
        if (!record.IsObject) return;
        var msg = record;
        if (msg.String("role") is null)
        {
            var inner = msg.Property("message");
            if (inner.IsObject && inner.String("role") is not null) msg = inner;
        }
        if (msg.String("role") is not { } role) return;

        long ts = AdapterJson.IsoUs(record.String("timestamp") ?? record.String("created_at"));
        if (ts == 0 && record.Int64("timestamp") is { } ms)
            ts = ms > 4_000_000_000L ? ms * 1000 : ms * 1_000_000;
        if (ts == 0 && record.Int64("ts") is { } sec) ts = sec * 1_000_000;
        if (ts > 0) { if (firstUs == 0) firstUs = ts; lastUs = ts; }

        string? text = null;
        var content = msg.Property("content");
        if (content.Kind == JsonAstKind.String) text = content.AsString();
        else if (content.IsArray)
        {
            var sb = new StringBuilder();
            foreach (var block in content.Items())
            {
                string? t = block.Kind == JsonAstKind.String ? block.AsString() : block.String("text");
                if (string.IsNullOrEmpty(t)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            if (sb.Length > 0) text = sb.ToString();
        }
        text ??= msg.String("text");
        if (string.IsNullOrEmpty(text)) return;

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        AdapterJson.CollectMeta(msg, meta, "role", "content", "text", "timestamp", "created_at");

        turns.Add(new AgentTurn(turns.Count, AgentRoles.Normalize(role), ts)
        {
            Text = text,
            Model = msg.String("model"),
            Meta = meta,
        });
    }
}
