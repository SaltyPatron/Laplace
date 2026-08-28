using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Mcp;

internal sealed record McpHttpOptions(string Token, string Origin, int Port = 5188,
    int MaxSessions = 32, int IdleMinutes = 30)
{
    public static McpHttpOptions FromEnvironment() => new(
        LaplaceInstall.TryReadConfig("LAPLACE_MCP_TOKEN", "mcp.env") ?? "",
        Environment.GetEnvironmentVariable("LAPLACE_MCP_ORIGIN") ?? "https://hart-server:8443");

    public void Validate()
    {
        if (Token.Length < 32 || Token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("LAPLACE_MCP_TOKEN must contain at least 32 non-whitespace characters.");
        if (!Uri.TryCreate(Origin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("LAPLACE_MCP_ORIGIN must be an HTTPS origin, without path or credentials.");
        if (Port is < 0 or > 65535 || MaxSessions is < 1 or > 256 || IdleMinutes is < 1 or > 120)
            throw new InvalidOperationException("Invalid MCP listener/session limits.");
    }
}

/// <summary>
/// MCP 2025-06-18 Streamable HTTP: POST replies use application/json; server-push
/// GET is explicitly unsupported (405), not the retired /sse transport. A session
/// owns the SAME dispatcher and tool surface used by STDIO. Requests in one session
/// serialize because the native writer and TurnCloser are not thread-safe.
/// </summary>
internal static class McpHttpHost
{
    internal const int BodyLimit = 1024 * 1024;

    public static WebApplication Build(McpHttpOptions options,
        Func<IMcpTools>? toolsFactory = null, Func<CancellationToken, Task<bool>>? readiness = null)
    {
        options.Validate();
        var database = toolsFactory is null || readiness is null ? ManagedServiceDatabase.Resolve() : null;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        // HTTP stays on loopback. The existing nginx terminates LAN TLS; no
        // ASPNETCORE_URLS override can accidentally publish the backend directly.
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.Port);
            k.Limits.MaxRequestBodySize = BodyLimit;
        });
        builder.Services.AddSingleton(_ => new McpSessions(options, toolsFactory ?? (() => new SubstrateTools(database))));
        var app = builder.Build();
        var checkReady = readiness ?? (ct => ReadyAsync(database!, ct));
        app.MapGet("/health/live", () => Results.Json(new { service = "laplace-mcp", live = true }));
        app.MapGet("/health/ready", async (CancellationToken ct) =>
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(5));
                var ready = await checkReady(budget.Token);
                return Results.Json(new { service = "laplace-mcp", ready }, statusCode: ready ? 200 : 503);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                app.Logger.LogWarning("MCP readiness failed: {ErrorType}", ex.GetType().Name);
                return Results.Json(new { service = "laplace-mcp", ready = false }, statusCode: 503);
            }
        });
        app.MapMethods("/mcp", ["POST", "GET", "DELETE", "OPTIONS"],
            (HttpContext context, McpSessions sessions) => HandleAsync(context, sessions, options));
        return app;
    }

    private static async Task<bool> ReadyAsync(string database, CancellationToken ct)
    {
        await using var db = LaplaceDataSource.Create(SubstrateAccess.Serving, database);
        await using var connection = await db.OpenConnectionAsync(ct);
        // Use the same typed inventory/perfcache probes as API readiness. The
        // health/audit tool performs an exact entity count even in shallow mode;
        // on a populated corpus that scan exceeds the readiness budget.
        return await ReadyFromProbesAsync(
            token => NpgsqlSubstrateReads.EntitiesAndConsensusExistAsync(connection, token),
            token => NpgsqlSubstrateReads.PerfCacheProbeAsync(connection, token), ct);
    }

    internal static async Task<bool> ReadyFromProbesAsync(
        Func<CancellationToken, Task<(bool EntitiesExist, bool ConsensusExist)>> inventory,
        Func<CancellationToken, Task<object?>> perfcache, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var present = await inventory(ct);
        if (!present.EntitiesExist || !present.ConsensusExist) return false;
        await perfcache(ct);
        // Inventory is estimated; readiness is not a full-corpus integrity claim.
        return true;
    }

    private static async Task HandleAsync(HttpContext context, McpSessions sessions, McpHttpOptions options)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        // Reject foreign and opaque origins, including on preflight. Do not enable
        // wildcard CORS. Non-browser MCP clients normally omit Origin altogether.
        if (request.Headers.TryGetValue("Origin", out var origins)
            && (origins.Count != 1 || !string.Equals(origins[0], options.Origin, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1 || !Authorized(authorization[0], options.Token))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"laplace-mcp\"";
            context.Response.StatusCode = 401;
            return;
        }
        if (request.Headers.TryGetValue("MCP-Protocol-Version", out var versions)
            && (versions.Count != 1 || versions[0] != McpServer.ProtocolVersion))
        {
            await Error(context, 400, "unsupported MCP protocol version");
            return;
        }
        if (request.Method is "GET" or "OPTIONS")
        {
            context.Response.Headers.Allow = "POST, DELETE";
            context.Response.StatusCode = 405;
            return;
        }
        var sessionId = request.Headers["Mcp-Session-Id"].ToString();
        if (request.Method == "DELETE")
        {
            context.Response.StatusCode = string.IsNullOrEmpty(sessionId) ? 400
                : await sessions.RemoveAsync(sessionId, context.RequestAborted) ? 204 : 404;
            return;
        }
        var accepted = request.GetTypedHeaders().Accept;
        if (accepted is null || !accepted.Any(a => a.MediaType == "application/json" && a.Quality != 0)
            || !accepted.Any(a => a.MediaType == "text/event-stream" && a.Quality != 0))
        {
            await Error(context, 406, "Accept must include application/json and text/event-stream");
            return;
        }
        if (!request.HasJsonContentType())
        {
            await Error(context, 415, "Content-Type must be application/json");
            return;
        }
        JsonObject? message;
        try
        {
            // Bound chunked requests too; Content-Length alone is not a limit.
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int size;
            while ((size = await request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
            {
                if (body.Length + size > BodyLimit) { context.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, size);
            }
            message = JsonNode.Parse(body.ToArray()) as JsonObject;
        }
        catch (JsonException)
        {
            await Error(context, 400, "invalid JSON", -32700);
            return;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            context.Response.StatusCode = 413;
            return;
        }
        if (message is null || message["jsonrpc"]?.ToJsonString() != "\"2.0\"")
        {
            await Error(context, 400, "expected one JSON-RPC 2.0 message");
            return;
        }
        var initialize = message["method"]?.ToJsonString() == "\"initialize\"" && message["id"] is not null;
        McpSession? session;
        if (initialize)
        {
            if (sessionId.Length != 0) { await Error(context, 400, "initialize must not carry a session id"); return; }
            session = await sessions.CreateAsync();
            if (session is null) { context.Response.StatusCode = 503; return; }
            context.Response.Headers["Mcp-Session-Id"] = session.Id;
        }
        else
        {
            if (sessionId.Length == 0) { await Error(context, 400, "Mcp-Session-Id is required"); return; }
            session = sessions.Find(sessionId);
            if (session is null) { context.Response.StatusCode = 404; return; }
        }
        await session.Gate.WaitAsync(context.RequestAborted);
        try
        {
            if (session.Closed) { context.Response.StatusCode = 404; return; }
            session.Touch();
            // A client disconnect is NOT cancellation of an accepted MCP tool.
            // Let the canonical handler finish; never release its writer gate early.
            string? reply;
            try { reply = await Task.Run(() => session.Server.Handle(message.ToJsonString())); }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("mcp")
                    .LogError("MCP handler failed: {ErrorType}", ex.GetType().Name);
                reply = McpServer.ErrorReply(message["id"]?.DeepClone(), -32603, "tool dispatch failed");
            }
            if (reply is null) { context.Response.StatusCode = 202; return; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(reply);
        }
        finally { session.Touch(); session.Gate.Release(); }
    }

    private static bool Authorized(string? value, string token)
    {
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Length > 4096)
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value[7..])),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static Task Error(HttpContext context, int status, string message, int code = -32600)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(McpServer.ErrorReply(null, code, message));
    }
}
