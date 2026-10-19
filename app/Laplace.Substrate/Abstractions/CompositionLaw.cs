using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A composition's identity is the ordered child-id sequence. Its floor is one
/// boundary above the highest constituent; a single constituent keeps its own floor.
/// The hash discards the tier byte (<c>hash128_merkle</c>). The floor is what
/// <c>entities.tier</c> records.
/// </summary>
public static class CompositionLaw
{
    public readonly record struct Face(Hash128 Id, byte Tier);

    public static bool TryFace(string? surface, out Face face)
    {
        face = default;
        if (string.IsNullOrEmpty(surface)) return false;
        using var tree = ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(surface));
        if (tree is null) return false;
        var node = tree.GetNode(tree.NaturalUnitIndex());
        face = new Face(node.Id, node.Tier);
        return true;
    }

    public static Face RequiredFace(string surface) =>
        TryFace(surface, out Face face)
            ? face
            : throw new InvalidOperationException($"content could not be composed: {surface}");

    public static byte ParentFloor(ReadOnlySpan<byte> tiers)
    {
        if (tiers.Length == 0)
            throw new ArgumentException("a composition needs a constituent", nameof(tiers));
        if (tiers.Length == 1) return tiers[0];
        byte max = 0;
        for (int i = 0; i < tiers.Length; i++)
            if (tiers[i] > max) max = tiers[i];
        return (byte)(max + 1);
    }
}
