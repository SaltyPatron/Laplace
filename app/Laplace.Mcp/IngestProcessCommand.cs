using System.Text.Json.Nodes;
using Laplace.Ops;

namespace Laplace.Mcp;

internal static class IngestProcessCommand
{
    internal static (string Content, bool IsError) Run(
        string source,
        string path,
        int timeoutSeconds,
        Func<IngestProcessRunner.StartReceipt> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(start);
        int boundedTimeout = Math.Max(1, timeoutSeconds);

        var started = start();
        bool timedOut = false;
        IngestProcessRunner.CompletionReceipt completed;
        try
        {
            completed = IngestProcessRunner.WaitForCompletionAsync(started.ProcessRunId)
                .WaitAsync(TimeSpan.FromSeconds(boundedTimeout))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            timedOut = true;
            try
            {
                IngestProcessRunner.Stop(started.ProcessId);
            }
            catch
            {
                // Preserve wait for observed child termination even if stop raced with exit.
            }
            completed = IngestProcessRunner.WaitForCompletionAsync(started.ProcessRunId)
                .GetAwaiter()
                .GetResult();
        }

        var row = new JsonObject
        {
            ["process_id"] = started.ProcessId,
            ["process_run_id"] = started.ProcessRunId.ToString(),
            ["source"] = source,
            ["path"] = path,
            ["status"] = completed.Status,
            ["timed_out"] = timedOut,
            ["exit_code"] = completed.ExitCode,
            ["output_incomplete"] = completed.OutputIncomplete,
            ["stdout"] = Tail(completed.Stdout),
            ["stderr"] = Tail(completed.Stderr),
        };

        var payload = new JsonObject { ["rows"] = new JsonArray(row) };
        bool isError = timedOut || completed.ExitCode != 0 || !string.Equals(completed.Status, "completed", StringComparison.Ordinal);
        return (payload.ToJsonString(), isError);
    }

    private static string Tail(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= 4000 ? value
        : value[^4000..];
}
