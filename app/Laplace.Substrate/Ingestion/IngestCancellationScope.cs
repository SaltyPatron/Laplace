using System.Runtime.InteropServices;

namespace Laplace.Ingestion;

/// <summary>
/// Keeps process signals inside an ingest's cancellation and receipt lifecycle.
/// Native and managed ingestion must unwind before the process exits.
/// </summary>
public sealed class IngestCancellationScope : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly PosixSignalRegistration? _terminate;
    private readonly PosixSignalRegistration? _interrupt;
    private readonly object _gate = new();
    private readonly bool _console;
    private bool _disposed;

    public CancellationToken Token => _source.Token;

    public IngestCancellationScope(CancellationToken cancellationToken = default)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        }
        else
        {
            _console = true;
            Console.CancelKeyPress += OnConsoleCancel;
        }
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Cancel();
    }

    private void OnConsoleCancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        Cancel();
    }

    private void Cancel()
    {
        lock (_gate)
            if (!_disposed) _source.Cancel();
    }

    public void Dispose()
    {
        _interrupt?.Dispose();
        _terminate?.Dispose();
        if (_console) Console.CancelKeyPress -= OnConsoleCancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}
