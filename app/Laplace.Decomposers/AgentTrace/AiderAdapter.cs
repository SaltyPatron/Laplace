using System.Runtime.CompilerServices;
using System.Text;

namespace Laplace.Decomposers.AgentTrace;

/// <summary>
/// Aider chat history: &lt;repo&gt;/.aider.chat.history.md — append-only Markdown holding
/// EVERY session of a repo. Session header `# aider chat started at YYYY-MM-DD
/// HH:MM:SS`; user input lines prefixed `#### `; assistant output raw markdown;
/// tool/system output as `&gt; ` blockquotes (aider/io.py). No structured tool calls,
/// usage, or model ids exist in this format. One file → one session per header.
/// </summary>
public sealed class AiderAdapter : IAgentTraceAdapter
{
    public string ProviderKey => "aider";

    private const string SessionHeader = "# aider chat started at ";

    public bool CanHandle(string filePath)
    {
        if (!Path.GetFileName(filePath).Equals(".aider.chat.history.md", StringComparison.Ordinal))
            return false;
        var line = AdapterJson.FirstLine(filePath);
        // The file opens with a blank line then the header; scan a few lines.
        foreach (var l in AdapterJson.FirstLines(filePath, 4))
            if (l.StartsWith(SessionHeader, StringComparison.Ordinal)) return true;
        return line is null;
    }

    /// <summary>Repo-local files; no stable home root — discovery is explicit-path only.</summary>
    public IEnumerable<string> DefaultRoots(string homeDir) => [];

    public async IAsyncEnumerable<AgentSession> ParseAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        // Session identity: file path + header ordinal (aider has no session ids).
        string fileKey = AgentTraceEmitter.SanitizeKey(
            Path.GetFileName(Path.GetDirectoryName(filePath) ?? "repo") ?? "repo");
        int sessionOrdinal = -1;

        List<AgentTurn>? turns = null;
        long startUs = 0, lastUs = 0;
        var user = new StringBuilder();
        var assistant = new StringBuilder();
        var tool = new StringBuilder();

        using var reader = new StreamReader(filePath);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith(SessionHeader, StringComparison.Ordinal))
            {
                if (turns is not null)
                {
                    FlushPending(turns, user, assistant, tool, lastUs);
                    if (turns.Count > 0)
                        yield return Build(fileKey, sessionOrdinal, startUs, lastUs, turns);
                }
                sessionOrdinal++;
                turns = [];
                startUs = lastUs = AdapterJson.IsoUs(
                    line[SessionHeader.Length..].Trim().Replace(' ', 'T'));
                continue;
            }
            if (turns is null) continue;

            if (line.StartsWith("#### ", StringComparison.Ordinal) || line == "####")
            {
                FlushRole(turns, assistant, AgentRoles.Assistant, lastUs);
                FlushRole(turns, tool, AgentRoles.System, lastUs);
                Append(user, line.Length > 5 ? line[5..] : "");
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                FlushRole(turns, user, AgentRoles.User, lastUs);
                FlushRole(turns, assistant, AgentRoles.Assistant, lastUs);
                Append(tool, line.Length > 2 ? line[2..] : "");
            }
            else
            {
                FlushRole(turns, user, AgentRoles.User, lastUs);
                FlushRole(turns, tool, AgentRoles.System, lastUs);
                Append(assistant, line);
            }
        }

        if (turns is not null)
        {
            FlushPending(turns, user, assistant, tool, lastUs);
            if (turns.Count > 0)
                yield return Build(fileKey, sessionOrdinal, startUs, lastUs, turns);
        }
    }

    private AgentSession Build(
        string fileKey, int ordinal, long startUs, long endUs, List<AgentTurn> turns) =>
        new(ProviderKey, $"{fileKey}.{ordinal}", startUs, endUs, turns);

    private static void Append(StringBuilder sb, string line)
    {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(line);
    }

    private static void FlushRole(List<AgentTurn> turns, StringBuilder sb, string role, long ts)
    {
        string text = sb.ToString().Trim();
        sb.Clear();
        if (text.Length == 0) return;
        turns.Add(new AgentTurn(turns.Count, role, ts) { Text = text });
    }

    private static void FlushPending(
        List<AgentTurn> turns, StringBuilder user, StringBuilder assistant, StringBuilder tool,
        long ts)
    {
        FlushRole(turns, user, AgentRoles.User, ts);
        FlushRole(turns, assistant, AgentRoles.Assistant, ts);
        FlushRole(turns, tool, AgentRoles.System, ts);
    }
}
