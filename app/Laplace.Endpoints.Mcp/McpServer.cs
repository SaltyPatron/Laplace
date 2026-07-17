using System.Text.Json;
using System.Text.Json.Nodes;

namespace Laplace.Endpoints.Mcp;

/// <summary>
/// Minimal MCP protocol handler: initialize / tools/list / tools/call / ping.
/// Tools-only capability; requests we don't know get -32601, unknown
/// notifications are ignored, and a reply is returned only for requests
/// (null for notifications), per JSON-RPC 2.0.
/// </summary>
internal sealed class McpServer(SubstrateTools tools)
{
    private const string ProtocolVersion = "2025-06-18";

    public string? Handle(string line)
    {
        JsonNode? msg;
        try
        {
            msg = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return ErrorReply(null, -32700, $"parse error: {ex.Message}");
        }

        var method = msg?["method"]?.GetValue<string>();
        var id = msg?["id"]?.DeepClone();
        var isNotification = msg?["id"] is null;
        if (method is null)
            return isNotification ? null : ErrorReply(id, -32600, "missing method");

        switch (method)
        {
            case "initialize":
                return Reply(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "laplace-substrate",
                        ["version"] = "0.1.0",
                    },
                });

            case "ping":
                return Reply(id, new JsonObject());

            case "tools/list":
                return Reply(id, new JsonObject { ["tools"] = tools.ListTools() });

            case "tools/call":
            {
                var name = msg?["params"]?["name"]?.GetValue<string>();
                if (name is null)
                    return ErrorReply(id, -32602, "tools/call requires params.name");
                var args = msg?["params"]?["arguments"] as JsonObject;
                var (text, isError) = tools.Call(name, args);
                return Reply(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text,
                    }),
                    ["isError"] = isError,
                });
            }

            default:
                return isNotification ? null : ErrorReply(id, -32601, $"unknown method: {method}");
        }
    }

    private static string Reply(JsonNode? id, JsonNode result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString(JsonSerializerOptions.Default);

    public static string ErrorReply(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(JsonSerializerOptions.Default);
}
