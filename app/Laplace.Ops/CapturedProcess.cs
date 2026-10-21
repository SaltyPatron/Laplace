using System.Diagnostics;
using System.Text;

namespace Laplace.Ops;

/// <summary>Owns a child and drains both pipes with bounded diagnostic memory and a finite drain deadline.</summary>
public sealed class CapturedProcess : IDisposable
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool OutputIncomplete);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly CancellationTokenSource _drain = new();
    private readonly object _gate = new();
    private bool _disposed;
    public int Id { get; }
    public Task<Result> Completion { get; }

    private CapturedProcess(Process process, int outputLimit, Action<string, string>? output)
    {
        _process = process;
        Id = process.Id;
        Completion = CompleteAsync(outputLimit, output);
    }

    public static CapturedProcess Start(ProcessStartInfo start, int outputLimit = 16 * 1024,
        Action<string, string>? output = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLimit);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return new CapturedProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Child process did not start."), outputLimit, output);
    }

    public async Task TerminateAsync()
    {
        lock (_gate)
        {
            if (_disposed) return; // Disposal follows observed completion only.
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        // A kill request is not proof of termination. Completion also observes pipe failures.
        await Completion.WaitAsync(CleanupTimeout).ConfigureAwait(false);
    }

    public void CloseStandardInput()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _process.StandardInput.Close();
        }
    }

    private async Task<Result> CompleteAsync(int limit, Action<string, string>? output)
    {
        var stdout = new BoundedText(limit);
        var stderr = new BoundedText(limit);
        var outTask = PumpAsync(_process.StandardOutput, stdout, "stdout", output);
        var errTask = PumpAsync(_process.StandardError, stderr, "stderr", output);
        var pipes = Task.WhenAll(outTask, errTask);
        var exited = _process.WaitForExitAsync();
        try
        {
            // Observe an early reader failure before a full pipe can block the child forever.
            var pending = new List<Task> { exited, outTask, errTask };
            while (!exited.IsCompleted)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
            await exited.ConfigureAwait(false);
            bool incomplete = false;
            try { await pipes.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                incomplete = true;
                _drain.Cancel();
                await pipes.ConfigureAwait(false);
            }
            return new Result(_process.ExitCode, stdout.ToString(), stderr.ToString(), incomplete);
        }
        catch
        {
            _drain.Cancel();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await exited.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                LaplaceLogging.LogWarning(typeof(CapturedProcess), cleanupFailure,
                    "Child termination after output failure failed: process={ChildProcessId}", Id);
            }
            // Observe both readers even if one failed first. Their exception is the primary failure.
            try { await pipes.ConfigureAwait(false); }
            catch (Exception readerFailure)
            {
                LaplaceLogging.LogWarning(typeof(CapturedProcess), readerFailure,
                    "Child output capture failed: process={ChildProcessId}", Id);
            }
            throw;
        }
    }

    private async Task PumpAsync(StreamReader reader, BoundedText captured, string stream,
        Action<string, string>? output)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer, _drain.Token).ConfigureAwait(false)) > 0)
            {
                var chunk = new string(buffer, 0, count);
                captured.Append(chunk);
                output?.Invoke(stream, chunk);
            }
        }
        catch (OperationCanceledException) when (_drain.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!Completion.IsCompleted)
                throw new InvalidOperationException("A running captured process must be terminated and observed before disposal.");
            _disposed = true;
            _process.Dispose();
            _drain.Dispose();
        }
    }

    private sealed class BoundedText(int limit)
    {
        private readonly StringBuilder _text = new();
        private bool _truncated;
        public void Append(string value)
        {
            _text.Append(value);
            if (_text.Length <= limit) return;
            _text.Remove(0, _text.Length - limit);
            _truncated = true;
        }
        public override string ToString() => (_truncated ? "[earlier output truncated]\n" : "") + _text;
    }
}
