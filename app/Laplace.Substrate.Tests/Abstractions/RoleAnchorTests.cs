using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

public sealed class RoleAnchorTests
{
    [Fact]
    public void Identity_IsParentScoped_CaseNormalized_AndDomainSeparated()
    {
        Hash128 parentA = Hash128.OfCanonical("fixture/parent/a");
        Hash128 parentB = Hash128.OfCanonical("fixture/parent/b");

        Hash128 agentA = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentA, "Agent")!.Value;
        Hash128 agentALower = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentA, "agent")!.Value;
        Hash128 agentB = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentB, "Agent")!.Value;
        Hash128 frameAgent = RoleAnchor.Id(RoleIdentityKind.FrameNet, parentA, "Agent")!.Value;

        Assert.Equal(agentA, agentALower);
        Assert.NotEqual(agentA, agentB);
        Assert.NotEqual(agentA, frameAgent);
    }

    [Fact]
    public void Emit_DeclaresOneGovernedRole_WithoutContentGeometry()
    {
        Hash128 source = Hash128.OfCanonical("fixture/source");
        Hash128 parent = Hash128.OfCanonical("fixture/roleset");
        var builder = new SubstrateChangeBuilder(source, "fixture", null);

        Hash128 role = RoleAnchor.Emit(
            builder, RoleIdentityKind.PropBank, parent, "ARG0",
            EntityTypeRegistry.PropBankRole, source, SourceTrust.AcademicCurated)!.Value;
        SubstrateChange change = builder.Build();

        EntityRow entity = Assert.Single(change.Entities, e => e.Id == role);
        Assert.Equal(EntityTypeRegistry.PropBankRole, entity.TypeId);
        Assert.DoesNotContain(change.Physicalities, p => p.EntityId == role);
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(entity.TypeId));
        Assert.Contains(change.Attestations, a =>
            a.SubjectId == role
            && a.TypeId == RelationTypeRegistry.RelationTypeId("IS_TYPED_AS")
            && a.ObjectId == EntityTypeRegistry.PropBankRole);
    }
}
