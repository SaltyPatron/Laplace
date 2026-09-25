using System.Globalization;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

public static class ModelCoordinates
{
    public static readonly Hash128 CircuitTypeId = EntityTypeRegistry.Id("Model_Circuit");

    public static Hash128 ScalarId(int value) =>
        ScalarId(value.ToString(CultureInfo.InvariantCulture));

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
            ?? throw new InvalidOperationException(
                $"structural component '{value}' did not build a content tree");
        TierNodeView node = tree.GetNode(tree.NaturalUnitIndex());
        return new OrderedCompositionComponent(
            node.Id, node.Tier,
            node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3],
            node.Atom, node.Tier == 0);
    }

    internal static OrderedCompositionComponent StageTextComponent(
        SubstrateChangeBuilder builder, string value, Hash128 sourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return ContentEmitter.StageComponent(builder, value, sourceId)
            ?? throw new InvalidOperationException(
                $"structural component '{value}' has no content root");
    }

    public static string NormalizePlane(string plane)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plane);
        return plane.Trim().ToLowerInvariant();
    }

    private static string[] CircuitPath(string plane, int layer, int head)
    {
        plane = NormalizePlane(plane);
        if (head >= 0 && layer < 0)
            throw new ArgumentException("a model head cannot exist without a layer");
        var parts = new List<string>(6) { "model-circuit", plane };
        if (layer >= 0)
        {
            parts.Add("layer");
            parts.Add(layer.ToString(CultureInfo.InvariantCulture));
        }
        if (head >= 0)
        {
            parts.Add("head");
            parts.Add(head.ToString(CultureInfo.InvariantCulture));
        }
        return parts.ToArray();
    }

    /// <summary>
    /// The model witness as the first constituent of every circuit it owns. The
    /// witness entity is declared with the placement of its name content, so the
    /// circuit composes over a real coordinate. Two checkpoints' "layer 3 head 5"
    /// are therefore different circuits: a structural address is source-scoped,
    /// never a cross-model identity (INVENTIONS #55, spec 09).
    /// </summary>
    public static OrderedCompositionComponent WitnessComponent(Hash128 witness, string witnessName)
    {
        OrderedCompositionComponent name = TextComponent(witnessName);
        return new OrderedCompositionComponent(
            witness, EntityTier.Word, name.CoordX, name.CoordY, name.CoordZ, name.CoordM);
    }

    public static Hash128 CircuitId(
        Hash128 witness, string witnessName, string plane, int layer, int head)
    {
        string[] path = CircuitPath(plane, layer, head);
        var components = new OrderedCompositionComponent[path.Length + 1];
        components[0] = WitnessComponent(witness, witnessName);
        for (int i = 0; i < path.Length; i++) components[i + 1] = TextComponent(path[i]);
        return OrderedComposition.ComposeBatch(
            [new OrderedCompositionRequest(
                components, CircuitTypeId, default, 0)])[0].Id;
    }

    public static OrderedCompositionResult StageCircuit(
        SubstrateChangeBuilder builder, Hash128 witness, string witnessName,
        string plane, int layer, int head, long observedAtUnixUs = 0)
    {
        ArgumentNullException.ThrowIfNull(builder);
        string[] path = CircuitPath(plane, layer, head);
        var components = new OrderedCompositionComponent[path.Length + 1];
        components[0] = WitnessComponent(witness, witnessName);
        for (int i = 0; i < path.Length; i++)
            components[i + 1] = StageTextComponent(builder, path[i], witness);
        var request = new OrderedCompositionRequest(
            components, CircuitTypeId, witness,
            observedAtUnixUs == 0 ? IngestClock.NowUnixUs() : observedAtUnixUs);
        var result = new OrderedCompositionResult[1];
        OrderedComposition.StageBatch(builder.ContentStage, [request], result);
        return result[0];
    }
}
