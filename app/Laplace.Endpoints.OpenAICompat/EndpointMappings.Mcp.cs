using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Endpoints.Mcp;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        app.MapMethods("/mcp", ["POST", "DELETE", "GET", "OPTIONS"], HandleAsync)
            .ExcludeFromDescription();
        app.MapMethods("/v1/mcp", ["POST", "DELETE", "GET", "OPTIONS"], HandleAsync)
            .ExcludeFromDescription();
    }

    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        McpServer.ProtocolVersion,
        "2025-11-25",
        "2025-03-26",
        "2024-11-05",
    };

    private static async Task HandleAsync(
        HttpContext context,
        McpSessions sessions,
        ITenantResolver tenantResolver,
        IOptions<LaplaceAuthOptions> authOptions)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        context.Response.Headers.CacheControl = "no-store";

        var allowedOrigin = LaplaceInstall.TryReadConfig("LAPLACE_MCP_ORIGIN") ?? LaplaceInstall.EndpointBaseUrl;
        var currentOrigin = $"{request.Scheme}://{request.Host}";
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1
                || (!string.Equals(origins[0], allowedOrigin, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(origins[0], currentOrigin, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = 403;
                return;
            }
        }

        if (request.Method is "GET" or "OPTIONS")
        {
            context.Response.Headers.Allow = "POST, DELETE";
            context.Response.StatusCode = 405;
            return;
        }

        try
        {
            if (!await IsAuthorizedAsync(context, tenantResolver, authOptions.Value, ct))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"laplace-mcp\"";
            context.Response.StatusCode = 401;
            return;
        }

        if (request.Headers.TryGetValue("MCP-Protocol-Version", out var versions)
            && (versions.Count != 1 || !SupportedProtocolVersions.Contains(versions[0] ?? "")))
        {
            await WriteJsonRpcErrorAsync(context, 400, "unsupported MCP protocol version");
            return;
        }

        var sessionId = request.Headers["Mcp-Session-Id"].ToString();
        if (request.Method == "DELETE")
        {
            context.Response.StatusCode = string.IsNullOrEmpty(sessionId) ? 400
                : await sessions.RemoveAsync(sessionId, ct) ? 204 : 404;
            return;
        }

        var accepted = request.GetTypedHeaders().Accept;
        if (accepted is { Count: > 0 }
            && !accepted.Any(a =>
                a.Quality != 0
                && (a.MediaType == "*/*"
                    || a.MediaType == "application/*"
                    || a.MediaType == "application/json"
                    || a.MediaType == "text/event-stream")))
        {
            await WriteJsonRpcErrorAsync(context, 406, "Accept must allow application/json or text/event-stream");
            return;
        }

        if (!request.HasJsonContentType())
        {
            await WriteJsonRpcErrorAsync(context, 415, "Content-Type must be application/json");
            return;
        }

        JsonObject? message;
        try
        {
            message = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (JsonException)
        {
            await WriteJsonRpcErrorAsync(context, 400, "invalid JSON", -32700);
            return;
        }

        if (message is null || message["jsonrpc"]?.ToJsonString() != "\"2.0\"")
        {
            await WriteJsonRpcErrorAsync(context, 400, "expected one JSON-RPC 2.0 message");
            return;
        }

        var initialize = message["method"]?.ToJsonString() == "\"initialize\"" && message["id"] is not null;
        McpSession? session;
        if (initialize)
        {
            session = await sessions.CreateAsync(ct: ct);
            if (session is null)
            {
                context.Response.StatusCode = 503;
                return;
            }
            context.Response.Headers["Mcp-Session-Id"] = session.Id;
        }
        else if (string.IsNullOrEmpty(sessionId))
        {
            await WriteJsonRpcErrorAsync(context, 400, "Mcp-Session-Id header is required after initialize");
            return;
        }
        else if ((session = await sessions.FindOrCreateAsync(sessionId, ct)) is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (session.Closed)
        {
            context.Response.StatusCode = 404;
            return;
        }
        session.Touch();
        string? reply;
        try
        {
            reply = await Task.Run(() => session.Server.Handle(message.ToJsonString()), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("mcp")
                .LogError("MCP handler failed: {ErrorType}", ex.GetType().Name);
            reply = McpServer.ErrorReply(message["id"]?.DeepClone(), -32603, "tool dispatch failed");
        }
        session.Touch();
        if (reply is null)
        {
            context.Response.StatusCode = 202;
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected or aborted in-flight request.
        }
    }

    private static async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        ITenantResolver tenantResolver,
        LaplaceAuthOptions authOptions,
        CancellationToken ct)
    {
        var mcpToken = LaplaceInstall.TryReadConfig("LAPLACE_MCP_TOKEN");
        var bearer = ExtractBearer(context.Request.Headers.Authorization.ToString());
        if (!string.IsNullOrWhiteSpace(mcpToken) && bearer is not null && FixedTimeEquals(bearer, mcpToken.Trim()))
            return true;

        if (!string.IsNullOrWhiteSpace(authOptions.OperatorToken))
        {
            var expected = authOptions.OperatorToken.Trim();
            var opHeader = context.Request.Headers["X-Laplace-Operator-Token"].ToString();
            if ((!string.IsNullOrEmpty(opHeader) && FixedTimeEquals(opHeader.Trim(), expected))
                || (bearer is not null && FixedTimeEquals(bearer, expected)))
                return true;
        }

        var tenant = await tenantResolver.ResolveAsync(context, ct);
        if (tenant.AuthKind == "invalid_key")
            return false;
        if (tenant.AuthKind is "api_key" or "browser" or "dev")
            return true;
        return !authOptions.RequiresIdentity && tenant.AuthKind == "header";
    }

    private static string? ExtractBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task WriteJsonRpcErrorAsync(HttpContext context, int status, string message, int code = -32600)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(McpServer.ErrorReply(null, code, message), context.RequestAborted);
    }
}
