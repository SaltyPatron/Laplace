using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Laplace.Endpoints.Mcp.Tests;

public sealed class HttpTransportTests
{
    private const string Token = "test-only-0123456789-abcdefghijklmnopqrstuvwxyz";

    private sealed class Tools : IMcpTools
    {
        public int Calls;
        public bool Disposed;
        public Action? BeforeCall;
        public JsonArray ListTools() => [new JsonObject { ["name"] = "probe", ["inputSchema"] = new JsonObject { ["type"] = "object" } }];
        public (string Text, bool IsError) Call(string name, JsonObject? args)
        {
            BeforeCall?.Invoke();
            return ($"{{\"calls\":{++Calls}}}", false);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Host : IAsyncDisposable
    {
        public WebApplication App { get; }
        public HttpClient Client { get; private set; } = null!;
        public List<Tools> Instances { get; } = [];
        public Host(int maxSessions = 32, bool ready = true) => App = McpHttpHost.Build(
            new(Token, "https://hart-server:8443", Port: 0, MaxSessions: maxSessions),
            () => { var t = new Tools(); Instances.Add(t); return t; }, _ => Task.FromResult(ready));
        public async Task StartAsync()
        {
            await App.StartAsync();
            Client = new HttpClient { BaseAddress = new Uri(App.Urls.Single()) };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            Client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        }
        public Task<HttpResponseMessage> Post(string json, string? session = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (session is not null)
            {
                request.Headers.Add("Mcp-Session-Id", session);
                request.Headers.Add("MCP-Protocol-Version", McpServer.ProtocolVersion);
            }
            return Client.SendAsync(request);
        }
        public async Task<string> Initialize()
        {
            using var reply = await Post("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Equal("application/json", reply.Content.Headers.ContentType?.MediaType);
            var body = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
            Assert.Equal(McpServer.ProtocolVersion, (string?)body["result"]?["protocolVersion"]);
            return reply.Headers.GetValues("Mcp-Session-Id").Single();
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Initialize_Notification_List_Call_Delete_UsesOneCanonicalSession()
    {
        await using var host = new Host();
        await host.StartAsync();
        var id = await host.Initialize();
        Assert.Equal(64, id.Length);
        using var notification = await host.Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", id);
        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
        Assert.Equal("", await notification.Content.ReadAsStringAsync());
        using var list = await host.Post("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", id);
        Assert.Contains("probe", await list.Content.ReadAsStringAsync());
        using var call = await host.Post("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"probe"}}""", id);
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Equal(1, host.Instances.Single().Calls);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Add("Mcp-Session-Id", id);
        using var removed = await host.Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.True(host.Instances.Single().Disposed);
        using var stale = await host.Post("""{"jsonrpc":"2.0","id":4,"method":"ping"}""", id);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task MissingOrWrongBearer_CannotAllocateOrCall(string? token)
    {
        await using var host = new Host();
        await host.StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
        using var response = await host.Post("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Instances);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("https://hart-server:8443.evil.example")]
    public async Task ForeignOriginRejectedEvenWithValidBearer(string origin)
    {
        await using var host = new Host();
        await host.StartAsync();
        host.Client.DefaultRequestHeaders.Add("Origin", origin);
        using var response = await host.Post("{}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.Instances);
    }

    [Fact]
    public async Task SessionsAreIsolatedAndBounded()
    {
        await using var host = new Host(maxSessions: 2);
        await host.StartAsync();
        var first = await host.Initialize();
        var second = await host.Initialize();
        Assert.NotEqual(first, second);
        using var call = await host.Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"probe"}}""", first);
        Assert.Equal(1, host.Instances[0].Calls);
        Assert.Equal(0, host.Instances[1].Calls);
        using var full = await host.Post("""{"jsonrpc":"2.0","id":3,"method":"initialize"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
    }

    [Fact]
    public async Task ProtocolHeadersAndMessageBoundariesAreEnforced()
    {
        await using var host = new Host();
        await host.StartAsync();
        using var missingSession = await host.Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.BadRequest, missingSession.StatusCode);
        using var batch = await host.Post("[]");
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        using var invalid = await host.Post("{");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        host.Client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "bogus");
        using var version = await host.Post("{}");
        Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);
        host.Client.DefaultRequestHeaders.Remove("MCP-Protocol-Version");
        host.Client.DefaultRequestHeaders.Accept.Clear();
        using var accept = await host.Post("{}");
        Assert.Equal(HttpStatusCode.NotAcceptable, accept.StatusCode);
    }

    [Fact]
    public async Task NoLegacySse_AndReadinessIsNotLiveness()
    {
        await using var host = new Host(ready: false);
        await host.StartAsync();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.GetAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/sse")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public void InvalidSecretFailsClosedBeforeListening() =>
        Assert.Throws<InvalidOperationException>(() => McpHttpHost.Build(new("", "https://hart-server:8443")));

    [Fact]
    public void StdioDispatcher_NeverCallsAToolFromANotification()
    {
        var tools = new Tools();
        var server = new McpServer(tools);
        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"probe"}}"""));
        Assert.Equal(0, tools.Calls);
        Assert.Contains("-32600", server.Handle("[]"));
        Assert.Contains("-32700", server.Handle("{"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBodiesAreRejectedBeforeSessionAllocation(bool chunked)
    {
        await using var host = new Host();
        await host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        { Content = new StringContent(new string('x', McpHttpHost.BodyLimit + 1), Encoding.UTF8, "application/json") };
        request.Headers.TransferEncodingChunked = chunked;
        using var reply = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.StatusCode);
        Assert.Empty(host.Instances);
    }

    [Fact]
    public async Task ASessionNeverRunsTwoCanonicalWritersConcurrently()
    {
        await using var host = new Host();
        await host.StartAsync();
        var session = await host.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int concurrent = 0, overlaps = 0;
        host.Instances[0].BeforeCall = () =>
        {
            if (Interlocked.Increment(ref concurrent) > 1) Interlocked.Increment(ref overlaps);
            entered.TrySetResult();
            try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            finally { Interlocked.Decrement(ref concurrent); }
        };
        const string call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"probe"}}""";
        var first = host.Post(call, session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = host.Post(call, session);
        try { await Task.Delay(100); }
        finally { release.Set(); }
        using var firstReply = await first;
        using var secondReply = await second;
        Assert.Equal(HttpStatusCode.OK, firstReply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondReply.StatusCode);
        Assert.Equal(0, overlaps);
        Assert.Equal(2, host.Instances[0].Calls);
    }
}
