namespace Laplace.Engine.Core;

/// <summary>
/// Process-wide gate for native entry points that touch shared global state (perfcache load,
/// chess merkle compose). Grammar compose uses per-instance handles and thread-local parsers —
/// parallel file workers must NOT serialize on this object.
/// </summary>
public static class LaplaceCoreGate
{
    public static readonly object Native = new();
}
