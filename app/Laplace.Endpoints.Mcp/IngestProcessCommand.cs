using System.Text.Json.Nodes;
using Laplace.Ops;

namespace Laplace.Endpoints.Mcp;

/// <summary>The synchronous MCP tool waits on the shared, controllable process owner.</summary>
internal static class IngestProcessCommand
{
    internal static (string Content, bool IsError) Run(string source, string path, int timeoutSeconds,
        Func<IngestProcessRunner.StartReceipt>? start = null)
    {
        var receipt = start is null ? IngestProcessRunner.Start(source, path) : start();
        var completed = IngestProcessRunner.WaitForCompletionAsync(receipt.ProcessRunId);
        bool timedOut = false;
        IngestProcessRunner.CompletionReceipt result;
        try
        {
            result = completed.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            timedOut = true;
            // Stop confirms real process completion; a kill failure propagates with
            // its diagnostic and keeps the child under operator control.
            IngestProcessRunner.Stop(receipt.ProcessId);
            result = completed.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        string combined = result.Stdout;
        if (result.Stderr.Length > 0) combined += "\n--- stderr ---\n" + result.Stderr;
        var response = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["source"] = source, ["path"] = path,
                ["process_run_id"] = receipt.ProcessRunId.ToString(), ["process_id"] = receipt.ProcessId,
                ["status"] = result.Status, ["timed_out"] = timedOut,
                ["exit_code"] = result.ExitCode, ["output_incomplete"] = result.OutputIncomplete,
                ["output"] = Tail(combined), ["stdout"] = Tail(result.Stdout), ["stderr"] = Tail(result.Stderr),
            }),
        };
        return (response.ToJsonString(), timedOut || result.Status != "completed");
    }

    private static string Tail(string value) => value.Length <= 4000
        ? value : "...[truncated]...\n" + value[^4000..];
}
