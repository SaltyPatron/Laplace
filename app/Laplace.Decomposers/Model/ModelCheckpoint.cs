using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// A checkpoint header is physical source provenance. Tensor payload ranges may be
// read by a numerical analyzer, but they do not become content entities, model
// knowledge, or calibrated testimony. The retained source structure is the ordered
// safetensors header: tensor path, dtype, dimensions, and declaration order.
//
// Native ordered composition owns the resulting Merkle identity, dynamic tier floor,
// and physical trajectory. Model/layer/head addresses remain source context; token
// evidence requires a separately calibrated contraction route.
public static class ModelCheckpoint
{
    public static readonly Hash128 TensorTypeId = EntityTypeRegistry.Id("Model_Tensor");
    public static readonly Hash128 CheckpointTypeId = EntityTypeRegistry.Id("Model_Checkpoint");

    // Compose the checkpoint header as source structure. Tensor data ranges are
    // deliberately absent: opaque weight bytes are a source witness, never a
    // knowledge entity. The ordered header path, dtype, and dimensions are enough
    // to retain inspectable checkpoint provenance without minting testimony.
    //
    // Header levels are staged in batches. The common native kernel determines every
    // parent floor and records the ordered/RLE trajectory once; this method does not
    // calculate parent identities or serialize private structure.
    public static Hash128 StageCheckpoint(
        SubstrateChangeBuilder builder,
        IReadOnlyList<SafetensorsContainerParser.TensorReference> tensors,
        Hash128 sourceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tensors);
        if (tensors.Count == 0) return default;

        var leaves = new Dictionary<string, OrderedCompositionComponent>(StringComparer.Ordinal);
        var pathRequests = new OrderedCompositionRequest[tensors.Count];
        var shapeRequests = new OrderedCompositionRequest[tensors.Count];
        var dtypeComponents = new OrderedCompositionComponent[tensors.Count];
        long observedAt = IngestClock.NowUnixUs();

        for (int i = 0; i < tensors.Count; i++)
        {
            var tensor = tensors[i];
            string[] segments = tensor.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                throw new InvalidDataException("safetensors tensor name is empty");
            var path = new OrderedCompositionComponent[segments.Length];
            for (int j = 0; j < path.Length; j++)
                path[j] = TextLeaf(builder, leaves, segments[j], sourceId);
            pathRequests[i] = new OrderedCompositionRequest(path, TensorPathTypeId, sourceId, observedAt);

            dtypeComponents[i] = TextLeaf(builder, leaves, tensor.Dtype, sourceId);
            var shape = new OrderedCompositionComponent[Math.Max(1, tensor.Shape.Length)];
            if (tensor.Shape.Length == 0)
                shape[0] = TextLeaf(builder, leaves, "scalar", sourceId);
            else
            {
                for (int j = 0; j < tensor.Shape.Length; j++)
                {
                    if (tensor.Shape[j] < 0)
                        throw new InvalidDataException("safetensors tensor dimension is negative");
                    shape[j] = TextLeaf(builder, leaves,
                        tensor.Shape[j].ToString(System.Globalization.CultureInfo.InvariantCulture), sourceId);
                }
            }
            shapeRequests[i] = new OrderedCompositionRequest(shape, TensorShapeTypeId, sourceId, observedAt);
        }

        var paths = ComposeBatch(builder, pathRequests);
        var shapes = ComposeBatch(builder, shapeRequests);
        var tensorRequests = new OrderedCompositionRequest[tensors.Count];
        for (int i = 0; i < tensors.Count; i++)
        {
            tensorRequests[i] = new OrderedCompositionRequest(
                [ComponentFor(pathRequests[i], paths[i]), dtypeComponents[i],
                 ComponentFor(shapeRequests[i], shapes[i])],
                TensorTypeId, sourceId, observedAt);
        }

        var tensorResults = ComposeBatch(builder, tensorRequests);
        var checkpointComponents = new OrderedCompositionComponent[tensorResults.Length];
        for (int i = 0; i < checkpointComponents.Length; i++)
            checkpointComponents[i] = AsComponent(tensorResults[i]);
        return ComposeBatch(builder,
            [new OrderedCompositionRequest(checkpointComponents, CheckpointTypeId, sourceId, observedAt)])[0].Id;
    }

    private static readonly Hash128 TensorPathTypeId = EntityTypeRegistry.Id("Model_TensorPath");
    private static readonly Hash128 TensorShapeTypeId = EntityTypeRegistry.ModelAxis;

    /// <summary>Pure native calculation for one already-declared tensor header.</summary>
    internal static OrderedCompositionResult ComposeTensorHeader(
        SafetensorsContainerParser.TensorReference tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        string[] segments = tensor.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidDataException("safetensors tensor name is empty");
        var pathComponents = new OrderedCompositionComponent[segments.Length];
        for (int i = 0; i < pathComponents.Length; i++)
            pathComponents[i] = ModelCoordinates.TextComponent(segments[i]);
        OrderedCompositionRequest pathRequest = new(pathComponents, TensorPathTypeId, default, 0);
        OrderedCompositionResult path = OrderedComposition.ComposeBatch([pathRequest])[0];

        var shapeComponents = new OrderedCompositionComponent[Math.Max(1, tensor.Shape.Length)];
        if (tensor.Shape.Length == 0)
            shapeComponents[0] = ModelCoordinates.TextComponent("scalar");
        else
        {
            for (int i = 0; i < tensor.Shape.Length; i++)
            {
                if (tensor.Shape[i] < 0) throw new InvalidDataException("safetensors tensor dimension is negative");
                shapeComponents[i] = ModelCoordinates.TextComponent(
                    tensor.Shape[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        OrderedCompositionRequest shapeRequest = new(shapeComponents, TensorShapeTypeId, default, 0);
        OrderedCompositionResult shape = OrderedComposition.ComposeBatch([shapeRequest])[0];
        OrderedCompositionComponent dtype = ModelCoordinates.TextComponent(tensor.Dtype);
        return OrderedComposition.ComposeBatch(
            [new OrderedCompositionRequest(
                [ComponentFor(pathRequest, path), dtype, ComponentFor(shapeRequest, shape)],
                TensorTypeId, default, 0)])[0];
    }

    private static OrderedCompositionComponent TextLeaf(
        SubstrateChangeBuilder builder,
        Dictionary<string, OrderedCompositionComponent> leaves,
        string value,
        Hash128 sourceId)
    {
        if (!leaves.TryGetValue(value, out var component))
        {
            component = ModelCoordinates.StageTextComponent(builder, value, sourceId);
            leaves.Add(value, component);
        }
        return component;
    }

    private static OrderedCompositionResult[] ComposeBatch(
        SubstrateChangeBuilder builder, IReadOnlyList<OrderedCompositionRequest> requests)
    {
        var results = new OrderedCompositionResult[requests.Count];
        OrderedComposition.StageBatch(builder.ContentStage, requests, results);
        return results;
    }

    // A singleton composition is the exact child. OrderedCompositionResult does
    // not carry atom metadata because a non-singleton parent cannot be tier 0, so
    // retain the original child representation for a singleton continuation.
    private static OrderedCompositionComponent ComponentFor(
        OrderedCompositionRequest request, OrderedCompositionResult result) =>
        request.Components.Length == 1 ? request.Components[0] : AsComponent(result);

    private static OrderedCompositionComponent AsComponent(OrderedCompositionResult result) =>
        new(result.Id, result.Tier, result.CoordX, result.CoordY, result.CoordZ, result.CoordM);

}
