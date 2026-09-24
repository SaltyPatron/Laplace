using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public enum LexicalMemberIdentityKind : ushort
{
    VerbNet = 1,
}

/// <summary>
/// A source lexical member is the witnessed structure [member-schema, system, owner,
/// source-member-key]. The key itself is canonical content and supplies the placement;
/// the owner/system remain exact trajectory constituents.
/// </summary>
public static class LexicalMemberAnchor
{
    private const string SchemaText = "lexical-member/structure/v2";

    public static Hash128? Id(
        LexicalMemberIdentityKind kind, Hash128 ownerId, string? rawMemberKey)
    {
        Validate(kind, ownerId);
        string? key = Normalize(rawMemberKey);
        if (key is null) return null;
        Hash128? member = ContentEmitter.RootId(key);
        if (member is null) return null;
        Span<Hash128> constituents = stackalloc Hash128[4]
        {
            RequiredRoot(SchemaText), RequiredRoot(KindMarkerText(kind)), ownerId, member.Value
        };
        return Hash128.Merkle(EntityTier.Word, constituents);
    }

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        LexicalMemberIdentityKind kind,
        Hash128 ownerId,
        string? memberKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Validate(kind, ownerId);
        string? key = Normalize(memberKey);
        if (key is null) return null;
        OrderedCompositionComponent schema =
            RequiredComponent(builder, SchemaText, source);
        OrderedCompositionComponent system =
            RequiredComponent(builder, KindMarkerText(kind), source);
        OrderedCompositionComponent? member =
            ContentEmitter.StageComponent(builder, key, source);
        if (member is not { } component) return null;

        Span<Hash128> constituents = stackalloc Hash128[4]
        {
            schema.Id, system.Id, ownerId, component.Id
        };
        Hash128 id = Hash128.Merkle(EntityTier.Word, constituents);
        builder.AddEntity(id, EntityTier.Word, entityTypeId);

        double[] coord = Math4d.KarcherMean(
        [
            schema.CoordX, schema.CoordY, schema.CoordZ, schema.CoordM,
            system.CoordX, system.CoordY, system.CoordZ, system.CoordM,
            component.CoordX, component.CoordY, component.CoordZ, component.CoordM,
        ]);
        builder.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.ParseStructure),
            id, source, PhysicalityType.ParseStructure,
            coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
            Trajectory.Build(constituents), constituents.Length,
            null, null, 0));
        CategoryAnchor.AttestCategory(builder, id, entityTypeId, source, trust);
        return id;
    }

    private static void Validate(LexicalMemberIdentityKind kind, Hash128 ownerId)
    {
        if (ownerId == default)
            throw new ArgumentException("member owner must not be empty", nameof(ownerId));
        if (kind != LexicalMemberIdentityKind.VerbNet)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown member identity domain");
    }

    private static string KindMarkerText(LexicalMemberIdentityKind kind) =>
        $"lexical-member/system/{(ushort)kind}/v1";

    private static Hash128 RequiredRoot(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException($"lexical-member constituent could not be composed: {value}");

    private static OrderedCompositionComponent RequiredComponent(
        SubstrateChangeBuilder builder, string value, Hash128 source) =>
        ContentEmitter.StageComponent(builder, value, source)
        ?? throw new InvalidOperationException($"lexical-member constituent could not be admitted: {value}");

    private static string? Normalize(string? rawMemberKey) =>
        string.IsNullOrWhiteSpace(rawMemberKey)
            ? null
            : rawMemberKey.Trim().Normalize(NormalizationForm.FormC);
}
