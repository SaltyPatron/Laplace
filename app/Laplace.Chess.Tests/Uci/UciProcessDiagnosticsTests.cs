using System.Collections.Concurrent;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Engines;
using Laplace.Chess.Uci.Lab;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Laplace.Chess.Uci.Tests;

[CollectionDefinition("UCI process diagnostics", DisableParallelization = true)]
public sealed class UciProcessDiagnosticsCollection;

[Collection("UCI process diagnostics")]
public sealed class UciProcessDiagnosticsTests
{
    [SkippableTheory]
    [InlineData(0, 0)]
    [InlineData(23, 2)]
    public void ProxyRetainsActualEngineStderrAndDoesNotHideItsFailedExit(int engineExit, int expectedExit)
    {
        string? executable = OperatingSystem.IsWindows() ? LabFiles.Which("pwsh") : "/bin/sh";
        Skip.If(executable is null || !File.Exists(executable), "The local process fixture needs PowerShell or /bin/sh.");
        string directory = Path.Combine(Path.GetTempPath(), "uci-process-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, OperatingSystem.IsWindows() ? "engine.ps1" : "engine.sh");
        var capture = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        Log.Logger = logger;
        try
        {
            File.WriteAllText(script, OperatingSystem.IsWindows() ? $$"""
                while (($line = [Console]::ReadLine()) -ne $null) {
                    switch ($line) {
                        'uci' { [Console]::WriteLine('id name Diagnostic fixture'); [Console]::WriteLine('uciok') }
                        'isready' { [Console]::WriteLine('readyok') }
                        'quit' { [Console]::Error.WriteLine('retained engine diagnostic'); exit {{engineExit}} }
                    }
                }
                """ : $$"""
                while IFS= read -r line; do
                    case "$line" in
                        uci) printf '%s\n' 'id name Diagnostic fixture' 'uciok' ;;
                        isready) printf '%s\n' 'readyok' ;;
                        quit) printf '%s\n' 'retained engine diagnostic' >&2; exit {{engineExit}} ;;
                    esac
                done
                """);
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script] : [script];
            var spec = new ChessEngineSpec("diagnostic-fixture", "test",
                new UciProcessStart(executable!, arguments, HandshakeTimeout: TimeSpan.FromSeconds(5)), null, [], 1);
            using var output = new StringWriter();
            int result = UciProxy.Run(spec, new Dictionary<string, string>(), new StringReader("uci\nquit\n"), output);
            Assert.Equal(expectedExit, result);
            Assert.True(SpinWait.SpinUntil(() => capture.Events.Any(IsEngineDiagnostic), TimeSpan.FromSeconds(5)),
                "The process stderr line never reached structured logging.");
            Assert.DoesNotContain("retained engine diagnostic", output.ToString());
            Assert.Contains("uciok", output.ToString());
        }
        finally
        {
            Log.Logger = previous;
            File.Delete(script);
            Directory.Delete(directory);
        }
    }

    private static bool IsEngineDiagnostic(LogEvent value) =>
        value.Properties.TryGetValue("EngineDiagnostic", out var diagnostic)
        && diagnostic is ScalarValue { Value: "retained engine diagnostic" };

    [Fact]
    public async Task OutputReaderFaultRetainsTheCauseInsteadOfBecomingOrdinaryEndOfStream()
    {
        var capture = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        Log.Logger = logger;
        try
        {
            using var reader = new FaultingReader();
            using var client = UciProcess.Attach(new StringWriter(), reader, TimeSpan.FromSeconds(5));
            reader.Fail();
            var failure = await Assert.ThrowsAsync<UciEngineException>(() => client.RelayOutput(_ => { }));
            Assert.Equal(UciFailure.Unavailable, failure.Failure);
            Assert.IsType<IOException>(failure.InnerException);
            Assert.Contains(capture.Events, value => value.Exception == failure.InnerException);
        }
        finally { Log.Logger = previous; }
    }

    private sealed class FaultingReader : TextReader
    {
        private int _line;
        private readonly TaskCompletionSource _fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fail() => _fail.TrySetResult();
        public override async Task<string?> ReadLineAsync()
        {
            if (_line++ == 0) return "id name Faulting fixture";
            if (_line == 2) return "uciok";
            await _fail.Task;
            throw new IOException("Controlled output reader failure.");
        }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
