using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Endpoints.Mcp;

// MCP stdio server over the substrate's SQL surface. Same shape as
// Laplace.Chess.Uci: a Console.ReadLine loop speaking a line protocol —
// here JSON-RPC 2.0, newline-delimited, per the MCP stdio transport.
// Protocol state and tool dispatch live in McpServer; substrate access in
// SubstrateTools. stdout carries protocol frames ONLY; diagnostics go to stderr.

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
        Console.Error.WriteLine($"laplace-mcp: unhandled {ex.GetType().Name}: {ex.Message}");
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
