using System.Text;
using Laplace.Decomposers.AgentTrace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Laplace.Decomposers.AgentTrace.Tests;

/// <summary>
/// Pure-parser tests: provider fixture → normalized model. Fixtures mirror the
/// on-disk schemas observed from real installations (August 2026).
/// </summary>
public sealed class AgentTraceAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "laplace-agents-" + Guid.NewGuid().ToString("N"));

    public AgentTraceAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        return path;
    }

    private static async Task<List<AgentSession>> ParseAllAsync(IAgentTraceAdapter adapter, string path)
    {
        var sessions = new List<AgentSession>();
        await foreach (var s in adapter.ParseAsync(path, CancellationToken.None))
            sessions.Add(s);
        return sessions;
    }

    // ── Claude Code ───────────────────────────────────────────────────────────────

    private const string ClaudeFixture =
        """
        {"type":"user","uuid":"u1","parentUuid":null,"sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:32:15.418Z","cwd":"/home/ahart/Projects/Laplace","gitBranch":"main","version":"2.1.0","userType":"external","message":{"role":"user","content":"run the tests"}}
        {"type":"assistant","uuid":"a1","parentUuid":"u1","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:32:20.000Z","cwd":"/home/ahart/Projects/Laplace","gitBranch":"main","message":{"role":"assistant","model":"claude-opus-5","stop_reason":"tool_use","usage":{"input_tokens":120,"output_tokens":45,"cache_read_input_tokens":1000,"cache_creation_input_tokens":50},"content":[{"type":"thinking","thinking":"the gate is ctest then dotnet","signature":"sig"},{"type":"text","text":"Running the gate now."},{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"just test"},"caller":"assistant"}]}}
        {"type":"user","uuid":"u2","parentUuid":"a1","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:33:00.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":[{"type":"text","text":"all green"}],"is_error":false}]}}
        {"type":"assistant","uuid":"a2","parentUuid":"u2","sessionId":"763989d6-3dd3-45f3-a5ef-9437faf5f921","timestamp":"2026-08-22T19:33:05.000Z","message":{"role":"assistant","model":"claude-opus-5","stop_reason":"end_turn","usage":{"input_tokens":200,"output_tokens":12,"cache_read_input_tokens":0,"cache_creation_input_tokens":0},"content":[{"type":"text","text":"Tests pass."}]}}
        """;

    [Fact]
    public async Task ClaudeCode_Parses_Turns_Tools_Usage_Timestamps()
    {
        string path = await WriteAsync("763989d6-3dd3-45f3-a5ef-9437faf5f921.jsonl", ClaudeFixture);
        var adapter = new ClaudeCodeAdapter();
        Assert.True(adapter.CanHandle(path));

        var sessions = await ParseAllAsync(adapter, path);
        var s = Assert.Single(sessions);
        Assert.Equal("claude-code", s.Provider);
        Assert.Equal("763989d6-3dd3-45f3-a5ef-9437faf5f921", s.SessionKey);
        Assert.Equal("/home/ahart/Projects/Laplace", s.Cwd);
        Assert.Equal("main", s.GitBranch);
        Assert.Equal("2.1.0", s.Meta["version"]);

        // Record 3 is pure tool_result plumbing — joined, not a turn.
        Assert.Equal(3, s.Turns.Count);
        Assert.Equal(AgentRoles.User, s.Turns[0].Role);
        Assert.Equal("run the tests", s.Turns[0].Text);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-22T19:32:15.418Z").ToUnixTimeMilliseconds() * 1000,
            s.Turns[0].TimestampUnixUs);

        var reply = s.Turns[1];
        Assert.Equal(AgentRoles.Assistant, reply.Role);
        Assert.Equal("claude-opus-5", reply.Model);
        Assert.Equal("tool_use", reply.StopReason);
        Assert.Equal("the gate is ctest then dotnet", reply.Thinking);
        Assert.Equal(120, reply.Usage!.InputTokens);
        Assert.Equal(45, reply.Usage.OutputTokens);
        Assert.Equal(1000, reply.Usage.CacheReadTokens);
        Assert.Equal(50, reply.Usage.CacheCreateTokens);

        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("Bash", call.Name);
        Assert.Contains("just test", call.InputJson);
        Assert.Equal("all green", call.ResultText);
        Assert.False(call.IsError);

        Assert.Equal("Tests pass.", s.Turns[2].Text);
    }

    [Fact]
    public async Task ClaudeCode_Sidechain_Gets_Its_Own_Session_Identity()
    {
        // A subagent transcript carries the PARENT's sessionId; keying on that alone
        // collapsed every subagent onto the parent and overwrote its trajectory.
        string path = await WriteAsync("agent-ab36cc5409c73748d.jsonl", ClaudeFixture);
        var s = Assert.Single(await ParseAllAsync(new ClaudeCodeAdapter(), path));
        Assert.Equal(
            "763989d6-3dd3-45f3-a5ef-9437faf5f921.agent-ab36cc5409c73748d", s.SessionKey);
        Assert.Equal("763989d6-3dd3-45f3-a5ef-9437faf5f921", s.Meta["sidechain_of"]);
    }

    // ── Codex ─────────────────────────────────────────────────────────────────────

    private const string CodexFixture =
        """
        {"timestamp":"2026-08-21T16:28:33.666Z","type":"session_meta","payload":{"session_id":"01a02526-d812-7250-ac55-ea3a1838f9d2","cwd":"/home/ahart/Projects/Laplace","originator":"codex-tui","cli_version":"0.148.0","model_provider":"openai"}}
        {"timestamp":"2026-08-21T16:28:35.000Z","type":"turn_context","payload":{"model":"gpt-5.2-codex","effort":"high","approval_policy":"on-request","cwd":"/home/ahart/Projects/Laplace"}}
        {"timestamp":"2026-08-21T16:28:36.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"what is this repo?"}]}}
        {"timestamp":"2026-08-21T16:28:40.000Z","type":"response_item","payload":{"type":"reasoning","summary":[{"type":"summary_text","text":"scan the README first"}],"encrypted_content":"xxx"}}
        {"timestamp":"2026-08-21T16:28:41.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"A knowledge substrate."}]}}
        {"timestamp":"2026-08-21T16:28:42.000Z","type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{\"command\":[\"cat\",\"README.md\"]}","call_id":"c1"}}
        {"timestamp":"2026-08-21T16:28:43.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"# Laplace"}}
        {"timestamp":"2026-08-21T16:28:44.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":900,"cached_input_tokens":100,"output_tokens":80,"reasoning_output_tokens":20,"total_tokens":1000},"last_token_usage":{"input_tokens":500,"cached_input_tokens":100,"output_tokens":40,"reasoning_output_tokens":10,"total_tokens":550}}}}
        """;

    [Fact]
    public async Task Codex_Parses_Rollout_With_Reasoning_Tools_Usage()
    {
        string path = await WriteAsync("rollout-2026-08-21T16-28-17-01a02526.jsonl", CodexFixture);
        var adapter = new CodexAdapter();
        Assert.True(adapter.CanHandle(path));

        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("codex", s.Provider);
        Assert.Equal("01a02526-d812-7250-ac55-ea3a1838f9d2", s.SessionKey);
        Assert.Equal("/home/ahart/Projects/Laplace", s.Cwd);
        Assert.Equal("codex-tui", s.Meta["originator"]);
        Assert.Equal("high", s.Meta["effort"]);

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("what is this repo?", s.Turns[0].Text);

        var reply = s.Turns[1];
        Assert.Equal("A knowledge substrate.", reply.Text);
        Assert.Equal("gpt-5.2-codex", reply.Model);
        Assert.Equal("scan the README first", reply.Thinking);
        Assert.Equal(500, reply.Usage!.InputTokens);
        Assert.Equal(100, reply.Usage.CacheReadTokens);

        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("shell", call.Name);
        Assert.Equal("# Laplace", call.ResultText);
    }

    // ── Gemini ────────────────────────────────────────────────────────────────────

    private const string GeminiFixture =
        """
        {
          "sessionId": "b82779af-3a1e-4f72-b5e7-60f1cf520744",
          "projectHash": "4b0c49138d3e7fbd",
          "startTime": "2026-01-30T00:57:40.322Z",
          "lastUpdated": "2026-01-30T02:10:30.210Z",
          "summary": "Unicode ingestion planning",
          "messages": [
            {"id":"m1","timestamp":"2026-01-30T00:57:40.322Z","type":"user","content":"hello"},
            {"id":"m2","timestamp":"2026-01-30T00:57:45.000Z","type":"gemini","content":"hi there","model":"gemini-3.6-pro","thoughts":"greet back","tokens":{"input":10,"output":5,"cached":2,"thoughts":3,"tool":0,"total":20},"toolCalls":[{"id":"t1","name":"read_file","args":{"path":"README.md"},"status":"success","result":"# Laplace","timestamp":"2026-01-30T00:57:44.000Z"}]}
          ]
        }
        """;

    [Fact]
    public async Task Gemini_Parses_Session_Document()
    {
        string path = await WriteAsync(Path.Combine("chats", "session-2026-01-30T00-53-b82779af.json"), GeminiFixture);
        var adapter = new GeminiAdapter();
        Assert.True(adapter.CanHandle(path));

        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("gemini", s.Provider);
        Assert.Equal("b82779af-3a1e-4f72-b5e7-60f1cf520744", s.SessionKey);
        Assert.Equal("Unicode ingestion planning", s.Title);
        Assert.Equal("4b0c49138d3e7fbd", s.Meta["projectHash"]);

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal(AgentRoles.User, s.Turns[0].Role);
        var reply = s.Turns[1];
        Assert.Equal(AgentRoles.Assistant, reply.Role);
        Assert.Equal("gemini-3.6-pro", reply.Model);
        Assert.Equal("greet back", reply.Thinking);
        Assert.Equal(10, reply.Usage!.InputTokens);
        Assert.Equal("3", reply.Meta["thoughts_tokens"]);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("# Laplace", call.ResultText);
        Assert.False(call.IsError);
    }

    // ── Antigravity ───────────────────────────────────────────────────────────────

    private const string AntigravityFixture =
        """
        {"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-08-03T09:30:49Z","content":"Why don't i have access?"}
        {"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-08-03T09:30:55Z","content":"Let me check your settings.","thinking":"user asks about model access"}
        {"step_index":2,"source":"MODEL","type":"RUN_COMMAND","status":"DONE","created_at":"2026-08-03T09:31:00Z","content":"gemini --version","exit_code":1,"tool_calls":[{"name":"run_command","args":{"command":"gemini --version"}}]}
        """;

    [Fact]
    public async Task Antigravity_Parses_Steps_And_Attaches_Tool_Steps()
    {
        string path = await WriteAsync(
            Path.Combine("brain", "f446d5cb-ca62-463c-9f25-217133e69c62", ".system_generated",
                "logs", "transcript_full.jsonl"),
            AntigravityFixture);
        var adapter = new AntigravityAdapter();
        Assert.True(adapter.CanHandle(path));

        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("antigravity", s.Provider);
        Assert.Equal("f446d5cb-ca62-463c-9f25-217133e69c62", s.SessionKey);

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal(AgentRoles.User, s.Turns[0].Role);
        var reply = s.Turns[1];
        Assert.Equal("user asks about model access", reply.Thinking);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("run_command", call.Name);
        Assert.True(call.IsError);
        Assert.Equal("gemini --version", call.ResultText);
    }

    [Fact]
    public async Task Antigravity_Truncated_View_Skipped_When_Full_Exists()
    {
        string full = await WriteAsync(
            Path.Combine("brain2", "logs", "transcript_full.jsonl"), AntigravityFixture);
        string truncated = await WriteAsync(
            Path.Combine("brain2", "logs", "transcript.jsonl"), AntigravityFixture);
        var adapter = new AntigravityAdapter();
        Assert.Empty(await ParseAllAsync(adapter, truncated));
        Assert.Single(await ParseAllAsync(adapter, full));
    }

    // ── Copilot ───────────────────────────────────────────────────────────────────

    private const string CopilotFixture =
        """
        {"type":"session.start","data":{"sessionId":"2e9d91dc-763e-4ddb-bdca-15778600772b","version":1,"producer":"copilot-agent","copilotVersion":"0.0.369","startTime":"2025-12-12T11:40:46.982Z"},"id":"e1","timestamp":"2025-12-12T11:40:46.984Z","parentId":null}
        {"type":"session.info","data":{"infoType":"authentication","message":"Logged in with gh as user: AHartTN"},"id":"e2","timestamp":"2025-12-12T11:40:47.000Z"}
        {"type":"user.message","data":{"content":"inspect this server","attachments":[]},"id":"e3","timestamp":"2025-12-12T11:41:00.000Z"}
        {"type":"assistant.turn_start","data":{"turnId":"0"},"id":"e4","timestamp":"2025-12-12T11:41:01.000Z"}
        {"type":"assistant.message","data":{"content":"Checking the host now.","messageId":"m1","toolRequests":[]},"id":"e5","timestamp":"2025-12-12T11:41:02.000Z"}
        {"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"bash","arguments":"{\"command\":\"uname -a\"}"},"id":"e6","timestamp":"2025-12-12T11:41:03.000Z"}
        {"type":"tool.execution_complete","data":{"toolCallId":"t1","success":true,"result":"Linux host 5.15"},"id":"e7","timestamp":"2025-12-12T11:41:04.000Z"}
        {"type":"assistant.turn_end","data":{"turnId":"0"},"id":"e8","timestamp":"2025-12-12T11:41:05.000Z"}
        """;

    [Fact]
    public async Task Copilot_Parses_Session_State_Events()
    {
        string path = await WriteAsync(
            Path.Combine("session-state", "2e9d91dc.jsonl"), CopilotFixture);
        var adapter = new CopilotAdapter();

        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("copilot", s.Provider);
        Assert.Equal("2e9d91dc-763e-4ddb-bdca-15778600772b", s.SessionKey);
        Assert.Equal("AHartTN", s.UserKey);
        Assert.Equal("0.0.369", s.Meta["copilotVersion"]);

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("inspect this server", s.Turns[0].Text);
        var call = Assert.Single(s.Turns[1].ToolCalls);
        Assert.Equal("bash", call.Name);
        Assert.Equal("Linux host 5.15", call.ResultText);
        Assert.False(call.IsError);
    }

    // ── Cursor ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cursor_Parses_Store_Db_Blobs()
    {
        string agentDir = Path.Combine(_dir, "chats", "wshash", "6199b207-8e5a-424a-9335-37dc93692e3e");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "meta.json"),
            """{"schemaVersion":1,"createdAtMs":1778453787169,"hasConversation":true,"title":"System Inspector","updatedAtMs":1778455612046}""");

        string dbPath = Path.Combine(agentDir, "store.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = "CREATE TABLE blobs (data BLOB); CREATE TABLE meta (data BLOB);";
            create.ExecuteNonQuery();
            foreach (string blob in (string[])
            [
                """{"role":"user","content":[{"type":"text","text":"review this system"}]}""",
                """{"role":"assistant","content":[{"type":"text","text":"Starting the review."},{"type":"tool-call","toolCallId":"tc1","toolName":"Shell","args":{"command":"uptime"}}]}""",
                """{"role":"tool","content":[{"type":"tool-result","toolCallId":"tc1","toolName":"Shell","result":"22:57:48 up 2 days"}]}""",
            ])
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO blobs (data) VALUES ($d)";
                insert.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(blob));
                insert.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        var adapter = new CursorAdapter();
        Assert.True(adapter.CanHandle(dbPath));

        var s = Assert.Single(await ParseAllAsync(adapter, dbPath));
        Assert.Equal("cursor", s.Provider);
        Assert.Equal("6199b207-8e5a-424a-9335-37dc93692e3e", s.SessionKey);
        Assert.Equal("System Inspector", s.Title);
        Assert.Equal(1778453787169L * 1000, s.StartedAtUnixUs);

        Assert.Equal(2, s.Turns.Count);
        var call = Assert.Single(s.Turns[1].ToolCalls);
        Assert.Equal("Shell", call.Name);
        Assert.Equal("22:57:48 up 2 days", call.ResultText);
    }

    // ── Generic fallback + routing ────────────────────────────────────────────────

    [Fact]
    public async Task Generic_Parses_OpenAI_Style_Messages_Document()
    {
        string path = await WriteAsync("export.json",
            """{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello","model":"some-model"}]}""");
        var adapter = new GenericJsonAdapter();
        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("generic", s.Provider);
        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("some-model", s.Turns[1].Model);
    }

    [Fact]
    public async Task Registry_Routes_Each_Fixture_To_Its_Adapter()
    {
        Assert.IsType<ClaudeCodeAdapter>(
            AgentTraceAdapters.Resolve(await WriteAsync("r1.jsonl", ClaudeFixture)));
        Assert.IsType<CodexAdapter>(
            AgentTraceAdapters.Resolve(await WriteAsync("rollout-r2.jsonl", CodexFixture)));
        Assert.IsType<CopilotAdapter>(
            AgentTraceAdapters.Resolve(await WriteAsync("r3.jsonl", CopilotFixture)));
        Assert.IsType<GenericJsonAdapter>(
            AgentTraceAdapters.Resolve(await WriteAsync("r4.jsonl",
                """{"role":"user","content":"plain"}""")));
    }
}

/// <summary>
/// The HAS_POS law, closed over the typed vocabulary: every relation the emitter can
/// reference must be declared in the source roster (and derive to a resolvable name).
/// </summary>
public sealed class AgentRelationVocabularyTests
{
    [Xunit.Fact]
    public void Every_Typed_Relation_Is_Declared_In_The_Source_Roster()
    {
        var declared = new HashSet<string>(AgentTraceSource.Relations, StringComparer.Ordinal);
        foreach (var relation in Enum.GetValues<AgentRelation>())
            Xunit.Assert.Contains(AgentRelations.Surface(relation), declared);
    }
}
