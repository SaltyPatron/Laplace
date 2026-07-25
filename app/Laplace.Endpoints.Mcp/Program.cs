using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Endpoints.Mcp;
using Laplace.Engine.Core.Ops;
using Microsoft.Extensions.Logging;

// MCP stdio server over the substrate's SQL surface. Same shape as
// Laplace.Chess.Uci: a Console.ReadLine loop speaking a line protocol —
// here JSON-RPC 2.0, newline-delimited, per the MCP stdio transport.
// Protocol state and tool dispatch live in McpServer; substrate access in
// SubstrateTools. stdout carries protocol frames ONLY; diagnostics go to the CSV
// ops sink (FileOnly — read back via ops.app_log, GH #602), never to a stream a
// client might read: the JSON-RPC error reply below is how the caller learns of a fault.

using var loggerFactory = LaplaceLogging.FileOnly("mcp");
var log = loggerFactory.CreateLogger("server");

var server = new McpServer(new SubstrateTools());
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
