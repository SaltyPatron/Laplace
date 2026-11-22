using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Hosting;
using Laplace.Mcp;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

namespace Laplace.Endpoints.Mcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--http"]))
        {
            return await LaplaceHost.RunAsync("mcp", args, async _ =>
            {
                await using var app = McpHttpHost.Build(McpHttpOptions.FromEnvironment());
                await app.RunAsync();
                return 0;
            });
        }

        ILoggerFactory? loggerFactory = null;
        int exitCode = 1;
        try
        {
            loggerFactory = LaplaceLogging.FileOnly("mcp");
            Npgsql.NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: false);
            if (args.Length != 0)
            {
                loggerFactory.CreateLogger("server").LogWarning("MCP started with unsupported command arguments.");
                Console.Error.WriteLine("usage: laplace-mcp [--http]");
                exitCode = 2;
            }
            else
            {
                exitCode = await RunStdioAsync(loggerFactory);
            }
        }
        catch (Exception ex)
        {
            LaplaceLogging.ReportProcessFailure("laplace-mcp", ex,
                failure => loggerFactory?.CreateLogger("server").LogCritical(failure, "MCP process terminated unexpectedly."));
            exitCode = 1;
        }
        return LaplaceLogging.CompleteProcess(loggerFactory, "laplace-mcp", exitCode);
    }

    private static async Task<int> RunStdioAsync(ILoggerFactory loggerFactory)
    {
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
            try { reply = server.Handle(line); }
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
    }

    private static JsonNode? TryId(string line)
    {
        try { return JsonNode.Parse(line)?["id"]?.DeepClone(); }
        catch (JsonException) { return null; }
    }
}
