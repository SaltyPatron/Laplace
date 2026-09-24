using System.Globalization;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public enum SemanticPredicateIdentityKind : ushort
{
    VerbNet = 1,
}

public readonly record struct SemanticPredicateArgument(string Type, string Value);

/// <summary>
/// One predicate occurrence is an exact ordered structure over its owner, frame/predicate
/// ordinals, predicate label and argument bindings. Identity is the shared Merkle
/// composition of those constituents and the stored ParseStructure physicality retains
/// the same trajectory.
/// </summary>
public static class SemanticPredicateAnchor
{
    private const string SchemaText = "semantic-predicate/structure/v2";

    public static Hash128 Id(
        SemanticPredicateIdentityKind kind,
        Hash128 ownerId,
        int frameOrdinal,
        int predicateOrdinal,
        Hash128 labelId,
        IReadOnlyList<SemanticPredicateArgument> arguments)
    {
        Validate(kind, ownerId, frameOrdinal, predicateOrdinal, labelId, arguments);
        Hash128 frame = RequiredRoot(frameOrdinal.ToString(CultureInfo.InvariantCulture));
        Hash128 predicate = RequiredRoot(predicateOrdinal.ToString(CultureInfo.InvariantCulture));
        var flat = new Hash128[6 + arguments.Count * 2];
        flat[0] = RequiredRoot(SchemaText);
        flat[1] = RequiredRoot(KindMarkerText(kind));
        flat[2] = ownerId;
        flat[3] = frame;
        flat[4] = predicate;
        flat[5] = labelId;
        int cursor = 6;
        foreach (SemanticPredicateArgument argument in arguments)
        {
            flat[cursor++] = RequiredRoot(Normalize(argument.Type));
            flat[cursor++] = RequiredRoot(Normalize(argument.Value));
        }
        return Hash128.Merkle(EntityTier.Word, flat);
    }

    public static Hash128 Declare(
        SubstrateChangeBuilder builder,
        SemanticPredicateIdentityKind kind,
        Hash128 ownerId,
        int frameOrdinal,
        int predicateOrdinal,
        OrderedCompositionComponent label,
        IReadOnlyList<SemanticPredicateArgument> arguments,
        Hash128 entityTypeId,
        Hash128 source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Validate(kind, ownerId, frameOrdinal, predicateOrdinal, label.Id, arguments);

        OrderedCompositionComponent schema =
            RequiredComponent(builder, SchemaText, source);
        OrderedCompositionComponent system =
            RequiredComponent(builder, KindMarkerText(kind), source);
        OrderedCompositionComponent frame = RequiredComponent(
            builder, frameOrdinal.ToString(CultureInfo.InvariantCulture), source);
        OrderedCompositionComponent predicate = RequiredComponent(
            builder, predicateOrdinal.ToString(CultureInfo.InvariantCulture), source);

        var flat = new Hash128[6 + arguments.Count * 2];
        flat[0] = schema.Id;
        flat[1] = system.Id;
        flat[2] = ownerId;
        flat[3] = frame.Id;
        flat[4] = predicate.Id;
        flat[5] = label.Id;

        var placed = new List<OrderedCompositionComponent>(5 + arguments.Count * 2)
        {
            schema, system, frame, predicate, label
        };
        int cursor = 6;
        foreach (SemanticPredicateArgument argument in arguments)
        {
            OrderedCompositionComponent type = RequiredComponent(
                builder, Normalize(argument.Type), source);
            OrderedCompositionComponent value = RequiredComponent(
                builder, Normalize(argument.Value), source);
            flat[cursor++] = type.Id;
            flat[cursor++] = value.Id;
            placed.Add(type);
            placed.Add(value);
        }

        Hash128 id = Hash128.Merkle(EntityTier.Word, flat);
        builder.AddEntity(id, EntityTier.Word, entityTypeId);

        var coords = new double[placed.Count * 4];
        for (int i = 0; i < placed.Count; i++)
        {
            OrderedCompositionComponent component = placed[i];
            coords[i * 4 + 0] = component.CoordX;
            coords[i * 4 + 1] = component.CoordY;
            coords[i * 4 + 2] = component.CoordZ;
            coords[i * 4 + 3] = component.CoordM;
        }
        double[] coord = Math4d.KarcherMean(coords);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.ParseStructure),
            id, source, PhysicalityType.ParseStructure,
            coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
            Trajectory.Build(flat), flat.Length, null, null, 0));
        return id;
    }

    private static void Validate(
        SemanticPredicateIdentityKind kind,
        Hash128 ownerId,
        int frameOrdinal,
        int predicateOrdinal,
        Hash128 labelId,
        IReadOnlyList<SemanticPredicateArgument> arguments)
    {
        if (kind != SemanticPredicateIdentityKind.VerbNet)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown predicate identity domain");
        if (ownerId == default)
            throw new ArgumentException("predicate owner must not be empty", nameof(ownerId));
        if (labelId == default)
            throw new ArgumentException("predicate label must not be empty", nameof(labelId));
        if (frameOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(frameOrdinal));
        if (predicateOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(predicateOrdinal));
        ArgumentNullException.ThrowIfNull(arguments);
    }

    private static string KindMarkerText(SemanticPredicateIdentityKind kind) =>
        $"semantic-predicate/system/{(ushort)kind}/v1";

    private static Hash128 RequiredRoot(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException("semantic predicate constituent has no content root");

    private static OrderedCompositionComponent RequiredComponent(
        SubstrateChangeBuilder builder, string value, Hash128 source) =>
        ContentEmitter.StageComponent(builder, value, source)
        ?? throw new InvalidOperationException("semantic predicate constituent could not be staged");

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
}
