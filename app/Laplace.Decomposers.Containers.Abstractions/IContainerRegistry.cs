namespace Laplace.Decomposers.Containers.Abstractions;

/// <summary>
/// Magic-byte → parser lookup per ADR 0055. The model-ingest path peeks
/// at a file's leading bytes and asks the registry which parser handles
/// the format; if no parser matches the file is logged + skipped (never
/// loaded).
/// </summary>
public interface IContainerRegistry
{
    /// <summary>All registered parsers. Order is registration order.</summary>
    IReadOnlyCollection<IContainerParser> Parsers { get; }

    /// <summary>Register a parser. Last-registered-wins for ambiguous
    /// magic prefixes (callers SHOULD use disjoint magic).</summary>
    void Register(IContainerParser parser);

    /// <summary>Returns the first registered parser whose
    /// <see cref="IContainerParser.CanParse"/> returns true for the magic
    /// prefix, or <c>null</c> if no parser matches.</summary>
    IContainerParser? Resolve(ReadOnlySpan<byte> magic);
}

/// <summary>Default <see cref="IContainerRegistry"/> implementation —
/// in-memory list, last-registered-wins, thread-safe registration
/// (lock-free reads).</summary>
public sealed class ContainerRegistry : IContainerRegistry
{
    private readonly List<IContainerParser> _parsers = new();
    private readonly object _lock = new();

    public IReadOnlyCollection<IContainerParser> Parsers
    {
        get
        {
            lock (_lock) return _parsers.ToArray();
        }
    }

    public void Register(IContainerParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        lock (_lock) _parsers.Add(parser);
    }

    public IContainerParser? Resolve(ReadOnlySpan<byte> magic)
    {
        // Walk from most-recent registration backward.
        IContainerParser[] snapshot;
        lock (_lock) snapshot = _parsers.ToArray();
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].CanParse(magic)) return snapshot[i];
        }
        return null;
    }
}
