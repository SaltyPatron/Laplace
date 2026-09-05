using System.Globalization;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Shared text-content components used by model recipes and native checkpoint
/// headers. Model layer/head labels remain source-header context; they are never
/// minted as token-evidence identities.
/// </summary>
public static class ModelCoordinates
{
    public static Hash128 ScalarId(int value) => ScalarId(value.ToString(CultureInfo.InvariantCulture));

    public static Hash128 ScalarId(string value)
    {
        if (!LlamaTokenizerParser.TryDecomposeRoot(Encoding.UTF8.GetBytes(value),
                out var id, out _, out _, out _, out _, out _))
            throw new InvalidOperationException($"scalar '{value}' failed content decomposition");
        return id;
    }

    internal static unsafe OrderedCompositionComponent TextComponent(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        using TierTree tree = ContentTierSpine.BuildTree(utf8)
            ?? throw new InvalidOperationException($"structural component '{value}' did not build a content tree");
        TierNodeView node = tree.GetNode(tree.NaturalUnitIndex());
        return new OrderedCompositionComponent(node.Id, node.Tier,
            node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3],
            node.Atom, node.Tier == 0);
    }

    internal static OrderedCompositionComponent StageTextComponent(
        SubstrateChangeBuilder builder, string value, Hash128 sourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (!ContentTierSpine.TryStageIntoBuilder(builder, utf8, sourceId, out Hash128 staged))
            throw new InvalidOperationException($"structural component '{value}' has no content root");
        OrderedCompositionComponent component = TextComponent(value);
        if (component.Id != staged)
            throw new InvalidOperationException($"structural component '{value}' changed identity during staging");
        return component;
    }
}
