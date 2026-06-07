namespace Laplace.Decomposers.Containers.Abstractions;

public interface IContainerRegistry
{
    IReadOnlyCollection<IContainerParser> Parsers { get; }

    void Register(IContainerParser parser);

    IContainerParser? Resolve(ReadOnlySpan<byte> magic);
}

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
        IContainerParser[] snapshot;
        lock (_lock) snapshot = _parsers.ToArray();
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].CanParse(magic)) return snapshot[i];
        }
        return null;
    }
}
