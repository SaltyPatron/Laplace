namespace Laplace.Decomposers.Abstractions;


public sealed class ArenaRmsTracker
{
    private readonly Dictionary<string, double> _sumSq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _count = new(StringComparer.Ordinal);

    public void Record(string typeName, double weight)
    {
        _sumSq.TryGetValue(typeName, out var sq);
        _count.TryGetValue(typeName, out var n);
        _sumSq[typeName] = sq + weight * weight;
        _count[typeName] = n + 1;
    }

    public double Scale(string typeName)
    {
        if (!_count.TryGetValue(typeName, out var n) || n <= 0) return 1.0;
        _sumSq.TryGetValue(typeName, out var sq);
        return Math.Sqrt(sq / n);
    }
}
