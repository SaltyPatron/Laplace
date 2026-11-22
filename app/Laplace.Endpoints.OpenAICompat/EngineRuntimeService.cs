using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Lazily initializes and holds the process-wide in-process C <see cref="ILaplaceEngineRuntime"/>
/// (<c>laplace_engine_abi</c>) without throwing during DI container construction or OpenAPI generation
/// when the native library or database is not yet attached.
/// </summary>
internal sealed class EngineRuntimeService : IDisposable
{
    private readonly Lazy<(ILaplaceEngineRuntime? Runtime, string? Unavailable)> _holder;

    public EngineRuntimeService(ILaplaceEngineRuntime? injected = null)
    {
        if (injected is not null)
        {
            _holder = new Lazy<(ILaplaceEngineRuntime?, string?)>(() => (injected, null));
            return;
        }

        _holder = new Lazy<(ILaplaceEngineRuntime?, string?)>(() =>
        {
            try
            {
                return (new LaplaceEngineRuntime(), null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return (null, ex.Message);
            }
        });
    }

    public ILaplaceEngineRuntime? Runtime => _holder.Value.Runtime;

    public string? Unavailable => _holder.Value.Unavailable;

    public void Dispose()
    {
        if (_holder.IsValueCreated)
            _holder.Value.Runtime?.Dispose();
    }
}
