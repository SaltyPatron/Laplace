using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

public sealed class RoleCorrespondenceIngestTests
{
    [Fact]
    public void Mapping_LiftsBothParentsIntoRoleIdentity_AndLeavesContextEmpty()
    {
        Hash128 source = Hash128.OfCanonical("fixture/source");
        var record = new RoleCorrespondenceRecord(
            "13.1-1", EntityTypeRegistry.VerbNetClass, "Agent",
            "Giving", EntityTypeRegistry.FrameNetFrame, "Donor");
        var handler = new RoleCorrespondenceHandler(
            source, RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO"),
            SourceTrust.AcademicCurated);
        using IIngestDeferredUnit unit = handler.CreateDeferredUnit(record);
        var builder = new SubstrateChangeBuilder(source, "fixture", null);

        unit.DrainInto(builder, 1.0, null);
        SubstrateChange change = builder.Build();

        Hash128 vnClass = AnchorAdmission.Id("13.1-1", EntityTypeRegistry.VerbNetClass)!.Value;
        Hash128 frame = AnchorAdmission.Id("Giving", EntityTypeRegistry.FrameNetFrame)!.Value;
        Hash128 vnRole = RoleAnchor.Id(RoleIdentityKind.VerbNet, vnClass, "Agent")!.Value;
        Hash128 frameRole = RoleAnchor.Id(RoleIdentityKind.FrameNet, frame, "Donor")!.Value;
        AttestationRow mapping = Assert.Single(change.Attestations);

        Assert.Null(mapping.ContextId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO"), mapping.TypeId);
        Assert.True(
            (mapping.SubjectId == vnRole && mapping.ObjectId == frameRole)
            || (mapping.SubjectId == frameRole && mapping.ObjectId == vnRole));
        Assert.Contains(change.Entities, e =>
            e.Id == vnRole && e.TypeId == EntityTypeRegistry.VerbNetRole);
        Assert.Contains(change.Entities, e =>
            e.Id == frameRole && e.TypeId == EntityTypeRegistry.FrameNetFe);
    }

    [Fact]
    public void SameLabelsUnderDifferentClasses_DoNotShareConsensusIdentity()
    {
        Hash128 source = Hash128.OfCanonical("fixture/source");
        var handler = new RoleCorrespondenceHandler(
            source, RelationTypeRegistry.RelationTypeId("ROLE_CORRESPONDS_TO"),
            SourceTrust.AcademicCurated);

        AttestationRow Map(string vnClass)
        {
            var record = new RoleCorrespondenceRecord(
                vnClass, EntityTypeRegistry.VerbNetClass, "Agent",
                "Giving", EntityTypeRegistry.FrameNetFrame, "Agent");
            using IIngestDeferredUnit unit = handler.CreateDeferredUnit(record);
            var builder = new SubstrateChangeBuilder(source, vnClass, null);
            unit.DrainInto(builder, 1.0, null);
            return Assert.Single(builder.Build().Attestations);
        }

        AttestationRow first = Map("9.1");
        AttestationRow second = Map("13.1");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.ContextId);
        Assert.Null(second.ContextId);
    }
}
