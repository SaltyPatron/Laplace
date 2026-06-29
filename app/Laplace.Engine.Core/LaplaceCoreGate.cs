namespace Laplace.Engine.Core;

/// <summary>
/// Single process-wide gate for all <c>laplace_core</c> entry points (grammar compose, intent_stage,
/// content witness bank, chess merkle compose). The native heap is not thread-safe; IngestRunner overlaps
/// decompose and apply on different threads, so every native P/Invoke must serialize on this object.
/// </summary>
public static class LaplaceCoreGate
{
    public static readonly object Native = new();
}
