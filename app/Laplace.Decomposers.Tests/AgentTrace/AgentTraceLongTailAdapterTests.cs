using System.Text;
using Laplace.Decomposers.AgentTrace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Laplace.Decomposers.AgentTrace.Tests;

/// <summary>
/// Long-tail adapters, fixtures transcribed from each project's cited serialization
/// source (see per-adapter headers): Aider, OpenCode, Cline/Roo, Goose, Amp,
/// Continue, Crush, Zed, Droid.
/// </summary>
public sealed class AgentTraceLongTailAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "laplace-agents-lt-" + Guid.NewGuid().ToString("N"));

    public AgentTraceLongTailAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private static SqliteConnection OpenNew(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    // ── Aider ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Aider_Parses_Markdown_Sessions()
    {
        string path = await WriteAsync(Path.Combine("repo", ".aider.chat.history.md"),
            """

            # aider chat started at 2026-08-01 10:00:00

            #### fix the flaky test
            #### please

            I'll pin the clock in the fixture.

            > Applied edit to tests/test_clock.py

            # aider chat started at 2026-08-02 11:30:00

            #### add a retry
            Sure — adding a bounded retry.
            """);
        var adapter = new AiderAdapter();
        Assert.True(adapter.CanHandle(path));

        var sessions = await ParseAllAsync(adapter, path);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal("aider", s.Provider));

        var first = sessions[0];
        Assert.Equal(3, first.Turns.Count);
        Assert.Equal(AgentRoles.User, first.Turns[0].Role);
        Assert.Equal("fix the flaky test\nplease", first.Turns[0].Text);
        Assert.Equal(AgentRoles.Assistant, first.Turns[1].Role);
        Assert.Equal(AgentRoles.System, first.Turns[2].Role);
        Assert.Contains("Applied edit", first.Turns[2].Text);
        Assert.True(first.StartedAtUnixUs > 0);

        Assert.Equal(2, sessions[1].Turns.Count);
        Assert.NotEqual(sessions[0].SessionKey, sessions[1].SessionKey);
    }

    // ── OpenCode (SQLite era) ─────────────────────────────────────────────────────

    [Fact]
    public async Task OpenCode_Parses_Sqlite_Session_Message_Part()
    {
        string dbPath = Path.Combine(_dir, "opencode.db");
        await using (var conn = OpenNew(dbPath))
        {
            Exec(conn, """
                CREATE TABLE session (id TEXT PRIMARY KEY, title TEXT, time_created INTEGER,
                    time_updated INTEGER, model TEXT);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, data TEXT,
                    time_created INTEGER);
                CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT, data TEXT);
                """);
            Exec(conn, "INSERT INTO session VALUES ('ses_abc','Fix the build',1755900000000,1755900500000,null)");
            Exec(conn, "INSERT INTO message VALUES ('msg_001','ses_abc','{\"role\":\"user\",\"time\":{\"created\":1755900000000}}',1755900000000)");
            Exec(conn, "INSERT INTO part VALUES ('prt_001','msg_001','{\"type\":\"text\",\"text\":\"why is the build red?\"}')");
            Exec(conn, "INSERT INTO message VALUES ('msg_002','ses_abc','{\"role\":\"assistant\",\"modelID\":\"claude-opus-5\",\"providerID\":\"anthropic\",\"cost\":0.12,\"tokens\":{\"input\":900,\"output\":120,\"reasoning\":0,\"cache\":{\"read\":500,\"write\":10}},\"time\":{\"created\":1755900100000}}',1755900100000)");
            Exec(conn, "INSERT INTO part VALUES ('prt_002','msg_002','{\"type\":\"text\",\"text\":\"A test import broke.\"}')");
            Exec(conn, "INSERT INTO part VALUES ('prt_003','msg_002','{\"type\":\"tool\",\"tool\":\"bash\",\"callID\":\"c1\",\"state\":{\"status\":\"completed\",\"input\":{\"command\":\"dotnet build\"},\"output\":\"1 error\",\"time\":{\"start\":1755900050000,\"end\":1755900060000}}}')");
        }
        SqliteConnection.ClearAllPools();

        var adapter = new OpenCodeAdapter();
        Assert.True(adapter.CanHandle(dbPath));
        var s = Assert.Single(await ParseAllAsync(adapter, dbPath));
        Assert.Equal("opencode", s.Provider);
        Assert.Equal("ses_abc", s.SessionKey);
        Assert.Equal("Fix the build", s.Title);
        Assert.Equal(2, s.Turns.Count);
        var reply = s.Turns[1];
        Assert.Equal("claude-opus-5", reply.Model);
        Assert.Equal(900, reply.Usage!.InputTokens);
        Assert.Equal(500, reply.Usage.CacheReadTokens);
        Assert.Equal(0.12, reply.Usage.CostUsd!.Value, 3);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("bash", call.Name);
        Assert.Equal("1 error", call.ResultText);
    }

    // ── Cline / Roo ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cline_Parses_Task_With_Usage_And_Model_Sidecars()
    {
        string taskDir = Path.Combine(_dir, "saoudrizwan.claude-dev", "tasks", "1755900000000");
        await WriteAsync(Path.Combine(taskDir, "ui_messages.json"),
            """[{"ts":1755900000000,"type":"say","say":"api_req_started","text":"{\"tokensIn\":800,\"tokensOut\":60,\"cacheReads\":100,\"cacheWrites\":5,\"cost\":0.034}"}]""");
        await WriteAsync(Path.Combine(taskDir, "task_metadata.json"),
            """{"model_usage":[{"ts":1755900000000,"model_id":"claude-opus-5","model_provider_id":"anthropic","mode":"act"}]}""");
        string apiPath = await WriteAsync(Path.Combine(taskDir, "api_conversation_history.json"),
            """
            [
              {"role":"user","content":[{"type":"text","text":"rename the module"}]},
              {"role":"assistant","content":[{"type":"text","text":"Renaming now."},{"type":"tool_use","id":"t1","name":"write_to_file","input":{"path":"a.cs"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}
            ]
            """);

        var adapter = new ClineAdapter();
        Assert.True(adapter.CanHandle(apiPath));
        var s = Assert.Single(await ParseAllAsync(adapter, apiPath));
        Assert.Equal("cline", s.Provider);
        Assert.Equal("1755900000000", s.SessionKey);
        Assert.Equal(2, s.Turns.Count); // tool_result record joins, not a turn
        var reply = s.Turns[1];
        Assert.Equal("claude-opus-5", reply.Model);
        Assert.Equal(800, reply.Usage!.InputTokens);
        Assert.Equal(0.034, reply.Usage.CostUsd!.Value, 3);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("write_to_file", call.Name);
        Assert.Equal("ok", call.ResultText);
    }

    [Fact]
    public async Task Roo_Task_Gets_Its_Own_Provider_Key()
    {
        string apiPath = await WriteAsync(
            Path.Combine("rooveterinaryinc.roo-cline", "tasks", "0198f0aa", "api_conversation_history.json"),
            """[{"role":"user","ts":1755900000000,"content":[{"type":"text","text":"hello roo"}]}]""");
        var s = Assert.Single(await ParseAllAsync(new ClineAdapter(), apiPath));
        Assert.Equal("roo-code", s.Provider);
        Assert.True(s.Turns[0].TimestampUnixUs > 0);
    }

    // ── Goose (legacy JSONL) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Goose_Parses_Legacy_Jsonl()
    {
        string path = await WriteAsync("20260801_101500.jsonl",
            """
            {"working_dir":"/home/ahart/proj","description":"fix ci","message_count":2,"total_tokens":900,"input_tokens":800,"output_tokens":100,"accumulated_total_tokens":900,"accumulated_input_tokens":800,"accumulated_output_tokens":100}
            {"id":"m1","role":"user","created":1754043300,"content":[{"type":"text","text":"why is ci red?"}]}
            {"id":"m2","role":"assistant","created":1754043330,"content":[{"type":"thinking","thinking":"check the runner"},{"type":"toolRequest","id":"r1","toolCall":{"status":"success","value":{"name":"shell","arguments":{"command":"gh run view"}}}}]}
            {"id":"m3","role":"user","created":1754043340,"content":[{"type":"toolResponse","id":"r1","toolResult":{"status":"success","value":{"content":[{"type":"text","text":"failed at step 3"}]}}}]}
            """);
        var adapter = new GooseAdapter();
        Assert.True(adapter.CanHandle(path));
        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("goose", s.Provider);
        Assert.Equal("/home/ahart/proj", s.Cwd);
        Assert.Equal("fix ci", s.Title);
        Assert.Equal(2, s.Turns.Count);
        Assert.Equal(1754043300L * 1_000_000, s.Turns[0].TimestampUnixUs);
        var reply = s.Turns[1];
        Assert.Equal("check the runner", reply.Thinking);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("shell", call.Name);
        Assert.Contains("failed at step 3", call.ResultText);
    }

    // ── Amp ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Amp_Parses_Thread_Json()
    {
        string path = await WriteAsync(Path.Combine("threads", "T-0aa8-4b:1.json").Replace(':', 'x'),
            """{"v":5,"id":"T-0aa8","created":1755900000000,"title":"audit the deploy","messages":[{"role":"user","content":"audit the deploy","meta":{"sentAt":1755900000000}},{"role":"assistant","content":[{"type":"thinking","thinking":"read the workflow"},{"type":"text","text":"Two findings."},{"type":"tool_use","id":"tu1","name":"Bash","input":{"cmd":"cat deploy.yml"}},{"type":"tool_result","tool_use_id":"tu1","content":"steps: ..."}],"usage":{"inputTokens":700,"outputTokens":90,"cacheReadInputTokens":300,"cacheCreationInputTokens":20}}]}""");
        var adapter = new AmpAdapter();
        Assert.True(adapter.CanHandle(path));
        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("amp", s.Provider);
        Assert.Equal("T-0aa8", s.SessionKey);
        Assert.Equal(2, s.Turns.Count);
        var reply = s.Turns[1];
        Assert.Equal("read the workflow", reply.Thinking);
        Assert.Equal(700, reply.Usage!.InputTokens);
        Assert.Equal(20, reply.Usage.CacheCreateTokens);
        Assert.Equal("steps: ...", Assert.Single(reply.ToolCalls).ResultText);
    }

    // ── Continue ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Continue_Parses_Session_History()
    {
        string path = await WriteAsync(Path.Combine("sessions", "0f9e7d.json"),
            """{"sessionId":"0f9e7d","title":"add logging","workspaceDirectory":"/home/ahart/proj","history":[{"message":{"role":"user","content":"add logging to startup"},"contextItems":[]},{"message":{"role":"assistant","content":"Adding a scoped logger.","toolCalls":[{"id":"c1","type":"function","function":{"name":"edit_file","arguments":"{\"path\":\"Program.cs\"}"}}],"usage":{"promptTokens":600,"completionTokens":80,"promptTokensDetails":{"cachedTokens":200}}},"contextItems":[],"toolCallStates":[{"toolCallId":"c1","status":"done","output":[{"content":"edited"}]}]}],"usage":{"promptTokens":600,"completionTokens":80,"totalCost":0.021}}""");
        var adapter = new ContinueAdapter();
        Assert.True(adapter.CanHandle(path));
        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("continue", s.Provider);
        Assert.Equal("/home/ahart/proj", s.Cwd);
        Assert.Equal("0.021", s.Meta["totalCost"]);
        var reply = s.Turns[1];
        Assert.Equal(600, reply.Usage!.InputTokens);
        Assert.Equal(200, reply.Usage.CacheReadTokens);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("edit_file", call.Name);
        Assert.Contains("edited", call.ResultText);
    }

    // ── Crush ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Crush_Parses_Sqlite_Sessions()
    {
        string dbPath = Path.Combine(_dir, ".crush", "crush.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var conn = OpenNew(dbPath))
        {
            Exec(conn, """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, title TEXT, created_at INTEGER,
                    updated_at INTEGER, cost REAL, prompt_tokens INTEGER, completion_tokens INTEGER);
                CREATE TABLE messages (id TEXT PRIMARY KEY, session_id TEXT, role TEXT,
                    parts TEXT, model TEXT, created_at INTEGER);
                """);
            Exec(conn, "INSERT INTO sessions VALUES ('s1','tidy the repo',1754043300,1754043400,0.05,1200,150)");
            Exec(conn, "INSERT INTO messages VALUES ('m1','s1','user','[{\"type\":\"text\",\"data\":{\"text\":\"tidy the repo\"}}]',null,1754043300)");
            Exec(conn, "INSERT INTO messages VALUES ('m2','s1','assistant','[{\"type\":\"tool_call\",\"data\":{\"id\":\"tc1\",\"name\":\"bash\",\"input\":\"{\\\"command\\\":\\\"git status\\\"}\"}},{\"type\":\"finish\",\"data\":{\"reason\":\"tool_use\"}}]','gpt-5.2',1754043330)");
            Exec(conn, "INSERT INTO messages VALUES ('m3','s1','tool','[{\"type\":\"tool_result\",\"data\":{\"tool_call_id\":\"tc1\",\"content\":\"clean\",\"is_error\":false}}]',null,1754043340)");
        }
        SqliteConnection.ClearAllPools();

        var adapter = new CrushAdapter();
        Assert.True(adapter.CanHandle(dbPath));
        var s = Assert.Single(await ParseAllAsync(adapter, dbPath));
        Assert.Equal("crush", s.Provider);
        Assert.Equal("0.05", s.Meta["totalCost"]);
        Assert.Equal(2, s.Turns.Count); // tool_result-only message joins its call
        var reply = s.Turns[1];
        Assert.Equal("gpt-5.2", reply.Model);
        Assert.Equal("tool_use", reply.StopReason);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("clean", call.ResultText);
        Assert.False(call.IsError);
    }

    // ── Zed ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zed_Parses_Zstd_Thread_Blob()
    {
        string thread =
            """{"version":"0.3.0","title":"profile the parser","updated_at":"2026-08-02T09:00:00Z","messages":[{"User":{"id":1,"content":[{"Text":"profile the parser"}]}},{"Agent":{"content":[{"Thinking":{"text":"flamegraph first","signature":null}},{"Text":"Hot spot is the lexer."},{"ToolUse":{"id":"zu1","name":"terminal","raw_input":"","input":{"command":"cargo flamegraph"},"is_input_complete":true}}],"tool_results":{"zu1":{"tool_use_id":"zu1","tool_name":"terminal","is_error":false,"content":"wrote flamegraph.svg","output":null}}}}],"cumulative_token_usage":{"input_tokens":1500,"output_tokens":200},"model":{"provider":"anthropic","model":"claude-opus-5"}}""";
        byte[] compressed = new ZstdSharp.Compressor(3).Wrap(Encoding.UTF8.GetBytes(thread)).ToArray();

        string dbPath = Path.Combine(_dir, "zed", "threads", "threads.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var conn = OpenNew(dbPath))
        {
            Exec(conn, "CREATE TABLE threads (id TEXT PRIMARY KEY, summary TEXT, updated_at TEXT, data_type TEXT, data BLOB)");
            Exec(conn, "INSERT INTO threads VALUES ('th-1','profile the parser','2026-08-02T09:00:00Z','zstd',$d)",
                ("$d", compressed));
        }
        SqliteConnection.ClearAllPools();

        var adapter = new ZedAdapter();
        Assert.True(adapter.CanHandle(dbPath));
        var s = Assert.Single(await ParseAllAsync(adapter, dbPath));
        Assert.Equal("zed", s.Provider);
        Assert.Equal("profile the parser", s.Title);
        Assert.Equal("1500", s.Meta["input_tokens"]);
        Assert.Equal(2, s.Turns.Count);
        var reply = s.Turns[1];
        Assert.Equal("flamegraph first", reply.Thinking);
        Assert.Equal("claude-opus-5", reply.Model);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("terminal", call.Name);
        Assert.Equal("wrote flamegraph.svg", call.ResultText);
    }

    // ── Droid ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Droid_Parses_Session_With_Settings_Sidecar()
    {
        string dir = Path.Combine(_dir, ".factory", "sessions", "-home-ahart-proj");
        await WriteAsync(Path.Combine(dir, "0aa8b1c2.settings.json"),
            """{"model":"claude-opus-5","reasoningEffort":"high","autonomyMode":"auto","tokenUsage":{"inputTokens":2000,"outputTokens":300,"cacheCreationTokens":40,"cacheReadTokens":900}}""");
        string path = await WriteAsync(Path.Combine(dir, "0aa8b1c2.jsonl"),
            """
            {"type":"session_start","id":"0aa8b1c2","title":"wire the cache","sessionTitle":"wire the cache","cwd":"/home/ahart/proj","version":"1.4.0","isSessionTitleManuallySet":false,"sessionTitleAutoStage":2}
            {"type":"message","id":"m1","timestamp":"2026-08-03T12:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"wire the cache"}]}}
            {"type":"message","id":"m2","timestamp":"2026-08-03T12:00:10.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"find the cache seam","signature":"s"},{"type":"text","text":"Wiring it now."},{"type":"tool_use","id":"d1","name":"Edit","input":{"path":"cache.ts"}}]}}
            {"type":"message","id":"m3","timestamp":"2026-08-03T12:00:20.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"d1","content":"edited"}]}}
            """);

        var adapter = new DroidAdapter();
        Assert.True(adapter.CanHandle(path));
        var s = Assert.Single(await ParseAllAsync(adapter, path));
        Assert.Equal("droid", s.Provider);
        Assert.Equal("0aa8b1c2", s.SessionKey);
        Assert.Equal("/home/ahart/proj", s.Cwd);
        Assert.Equal("high", s.Meta["reasoningEffort"]);
        Assert.Equal(2, s.Turns.Count);
        var reply = s.Turns[1];
        Assert.Equal("claude-opus-5", reply.Model);
        Assert.Equal("find the cache seam", reply.Thinking);
        Assert.Equal(2000, reply.Usage!.InputTokens); // sidecar totals on last assistant turn
        Assert.Equal("edited", Assert.Single(reply.ToolCalls).ResultText);
    }

    // ── routing sanity across the widened registry ────────────────────────────────

    [Fact]
    public async Task Registry_Still_Routes_Specifics_Before_Generic()
    {
        string aider = await WriteAsync(Path.Combine("r2", ".aider.chat.history.md"),
            "\n# aider chat started at 2026-08-01 10:00:00\n\n#### hi\nhello\n");
        Assert.IsType<AiderAdapter>(AgentTraceAdapters.Resolve(aider));

        string droid = await WriteAsync(Path.Combine("r2", "d.jsonl"),
            """{"type":"session_start","id":"x","sessionTitleAutoStage":1}""");
        Assert.IsType<DroidAdapter>(AgentTraceAdapters.Resolve(droid));

        string generic = await WriteAsync(Path.Combine("r2", "g.jsonl"),
            """{"role":"user","content":"plain"}""");
        Assert.IsType<GenericJsonAdapter>(AgentTraceAdapters.Resolve(generic));
    }
}
