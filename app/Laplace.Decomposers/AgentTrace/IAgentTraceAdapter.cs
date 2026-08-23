using System.Globalization;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// One provider's on-disk session format. Adapters are PURE parsers: file → normalized
/// <see cref="AgentSession"/> stream. No SQL, no batching, no substrate types — the
/// shared spine and <see cref="AgentTraceEmitter"/> own everything downstream. JSON is
/// navigated through the registered grammar route (<see cref="JsonAstDocument"/>), never
/// a hand-rolled parser.
/// </summary>
public interface IAgentTraceAdapter
{
    /// <summary>Tenant key ([A-Za-z0-9._@-], stable forever — it is witness identity).</summary>
    string ProviderKey { get; }

    /// <summary>Cheap path-shape test (name/dir patterns, optional first-bytes sniff).</summary>
    bool CanHandle(string filePath);

    /// <summary>Default session roots under a home directory, for path-less discovery runs.</summary>
    IEnumerable<string> DefaultRoots(string homeDir);

    IAsyncEnumerable<AgentSession> ParseAsync(string filePath, CancellationToken ct);
}

public static class AgentTraceAdapters
{
    /// <summary>
    /// Ordered registry: specific formats first, the generic JSON/JSONL fallback last so
    /// no role-shaped log is ever omitted. First CanHandle wins.
    /// </summary>
    public static IReadOnlyList<IAgentTraceAdapter> All { get; } =
    [
        new ClaudeCodeAdapter(),
        new CodexAdapter(),
        new GeminiAdapter(),
        new AntigravityAdapter(),
        new CopilotAdapter(),
        new CursorAdapter(),
        new GenericJsonAdapter(),
    ];

    public static IAgentTraceAdapter? Resolve(string filePath)
    {
        foreach (var a in All)
            if (a.CanHandle(filePath)) return a;
        return null;
    }
}

/// <summary>Shared parsing helpers for adapters. All tolerant: absent/malformed → null.</summary>
internal static class AdapterJson
{
    /// <summary>ISO-8601 → unix microseconds; 0 when absent/unparseable.</summary>
    internal static long IsoUs(string? iso) =>
        iso is not null
        && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds() * 1000
            : 0;

    internal static long MsUs(long? unixMs) => unixMs is { } ms ? ms * 1000 : 0;

    /// <summary>
    /// Scalar JSON values of an object as "key=value" metadata, skipping listed keys.
    /// Values clamp to 512 chars — metadata retention, not content duplication.
    /// </summary>
    internal static void CollectMeta(
        in JsonAstCursor obj, IDictionary<string, string> into, params string[] skip)
    {
        if (!obj.IsObject) return;
        foreach (var (key, value) in obj.Pairs())
        {
            if (Array.IndexOf(skip, key) >= 0) continue;
            string? v = value.Kind switch
            {
                JsonAstKind.String => value.AsString(),
                JsonAstKind.Number => value.RawText(),
                JsonAstKind.True => "true",
                JsonAstKind.False => "false",
                _ => null,
            };
            if (string.IsNullOrEmpty(v)) continue;
            into[key] = v.Length <= 512 ? v : v[..512];
        }
    }

    internal static async IAsyncEnumerable<JsonAstDocument> ReadJsonlAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(filePath);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            if (JsonAstDocument.TryParse(line) is { } doc) yield return doc;
        }
    }

    /// <summary>First non-empty line, for cheap format sniffs. Null on IO failure.</summary>
    internal static string? FirstLine(string filePath, int maxBytes = 4096)
    {
        foreach (var line in FirstLines(filePath, 1, maxBytes)) return line;
        return null;
    }

    /// <summary>
    /// Up to <paramref name="count"/> leading lines for format sniffs. A Claude Code
    /// transcript can OPEN with summary/file-history-snapshot records that carry none of
    /// the envelope keys, so single-line sniffs misroute real files (measured: this
    /// session's own transcript fell through to the generic adapter).
    /// </summary>
    internal static IEnumerable<string> FirstLines(string filePath, int count, int maxBytes = 65536)
    {
        var lines = new List<string>(count);
        try
        {
            using var reader = new StreamReader(filePath);
            var buf = new char[maxBytes];
            int n = reader.Read(buf, 0, maxBytes);
            int start = 0;
            for (int i = 0; i < n && lines.Count < count; i++)
            {
                if (buf[i] != '\n') continue;
                if (i > start) lines.Add(new string(buf, start, i - start));
                start = i + 1;
            }
            if (lines.Count < count && n > start) lines.Add(new string(buf, start, n - start));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }

    /// <summary>First bytes of the file as text, for whole-document sniffs.</summary>
    internal static string? Head(string filePath, int maxBytes = 512)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buf = new byte[maxBytes];
            int n = fs.Read(buf, 0, maxBytes);
            return n <= 0 ? null : System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
