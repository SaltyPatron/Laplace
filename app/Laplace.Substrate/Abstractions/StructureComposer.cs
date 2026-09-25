using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A structured source value is an ordered composition of its parts, never a joined
/// string (INVENTION §3). The composition's identity is Merkle over the ordered child
/// ids; it is admitted with a physicality whose trajectory is the exact ordered
/// constituent manifest and whose coordinate is the Karcher mean of its parts.
/// A single part collapses to itself.
/// </summary>
public static class StructureComposer
{
    /// <summary>A content part (a word, a label, a grammatical function), admitted.</summary>
    public static OrderedCompositionComponent? Text(SubstrateChangeBuilder builder, string? text, Hash128 source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ContentEmitter.StageComponent(builder, text.Trim(), source);
    }

    public static Hash128 Id(ReadOnlySpan<Hash128> parts) =>
        parts.Length == 1 ? parts[0] : Hash128.Merkle(EntityTier.Word, parts);

    public static OrderedCompositionComponent Compose(
        SubstrateChangeBuilder builder, Hash128 entityTypeId, Hash128 source,
        IReadOnlyList<OrderedCompositionComponent> parts)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (parts is not { Count: > 0 })
            throw new ArgumentException("a composition needs at least one part", nameof(parts));
        if (parts.Count == 1) return parts[0];
        var ids = new Hash128[parts.Count];
        var coords = new double[parts.Count * 4];
        byte tier = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            ids[i] = parts[i].Id;
            coords[i * 4] = parts[i].CoordX;
            coords[i * 4 + 1] = parts[i].CoordY;
            coords[i * 4 + 2] = parts[i].CoordZ;
            coords[i * 4 + 3] = parts[i].CoordM;
            if (parts[i].Tier > tier) tier = parts[i].Tier;
        }
        tier = (byte)Math.Min(tier + 1, byte.MaxValue);
        Hash128 id = Hash128.Merkle(EntityTier.Word, ids);
        builder.AddEntity(id, tier, entityTypeId);
        double[] c = Math4d.KarcherMean(coords);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.ParseStructure),
            id, source, PhysicalityType.ParseStructure,
            c[0], c[1], c[2], c[3], Hilbert128.Encode(c),
            Trajectory.Build(ids), ids.Length,
            null, null, 0));
        return new OrderedCompositionComponent(id, tier, c[0], c[1], c[2], c[3]);
    }
}
