using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Atomic2020;

internal ref struct Atomic2020TsvRow
{
    public ReadOnlySpan<byte> Head { get; init; }
    public ReadOnlySpan<byte> Relation { get; init; }
    public ReadOnlySpan<byte> Tail { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> line, out Atomic2020TsvRow row)
    {
        row = default;
        if (!TsvSpan.TryField(line, 0, out var head) || head.IsEmpty) return false;
        if (!TsvSpan.TryField(line, 1, out var rel) || rel.IsEmpty) return false;
        if (!TsvSpan.TryField(line, 2, out var tail)) return false;
        row = new Atomic2020TsvRow { Head = head, Relation = rel, Tail = tail };
        return true;
    }

    public string RelationText() => Encoding.UTF8.GetString(Relation).Trim();
    public string TailText() => Encoding.UTF8.GetString(Tail).Trim();
}
