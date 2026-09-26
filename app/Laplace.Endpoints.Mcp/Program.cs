using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Endpoints.Mcp;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

// No arguments: stdio transport. --http: the Streamable HTTP host, run as its own
// process.
if (args.SequenceEqual(new[] { "--http" }))
{
    await using var app = McpHttpHost.Build(McpHttpOptions.FromEnvironment());
    await app.RunAsync();
    return 0;
}
if (args.Length != 0)
{
    Console.Error.WriteLine("usage: laplace-mcp [--http]");
    return 2;
}

// MCP stdio transport: newline-delimited JSON-RPC 2.0 read from stdin, dispatched by
// McpServer onto SubstrateTools, the same machine operations every other interface
// calls. stdout carries protocol frames only; diagnostics go to the CSV ops sink
// (ops.app_log), and a fault reaches the caller as a JSON-RPC error reply.

using var loggerFactory = LaplaceLogging.FileOnly("mcp");
var log = loggerFactory.CreateLogger("server");

Console.Error.WriteLine($"[mcp] starting server — binary={Environment.ProcessPath ?? AppContext.BaseDirectory}");
log.LogInformation("starting mcp server, binary={BinaryPath}", Environment.ProcessPath ?? AppContext.BaseDirectory);

await using var tools = new SubstrateTools();
var server = new McpServer(tools);
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (line.Length == 0) continue;
    string? reply;
    try
    {
        reply = server.Handle(line);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "unhandled exception dispatching request");
        reply = McpServer.ErrorReply(TryId(line), -32603, ex.Message);
    }

    if (reply is not null)
    {
        Console.Out.WriteLine(reply);
        Console.Out.Flush();
    }
}

return 0;

static JsonNode? TryId(string line)
{
    try { return JsonNode.Parse(line)?["id"]?.DeepClone(); }
    catch (JsonException) { return null; }
}
