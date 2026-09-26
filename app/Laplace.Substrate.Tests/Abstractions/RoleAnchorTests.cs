using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

public sealed class RoleAnchorTests
{
    // The label is exact canonical content (NFC, surrounding space trimmed): "Agent" and
    // "agent" are different content, so different roles.
    [Fact]
    public void Identity_IsParentScoped_ContentExact_AndDomainSeparated()
    {
        Hash128 parentA = Hash128.OfCanonical("fixture/parent/a");
        Hash128 parentB = Hash128.OfCanonical("fixture/parent/b");

        Hash128 agentA = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentA, "Agent")!.Value;
        Hash128 agentASpaced = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentA, " Agent ")!.Value;
        Hash128 agentALower = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentA, "agent")!.Value;
        Hash128 agentB = RoleAnchor.Id(RoleIdentityKind.VerbNet, parentB, "Agent")!.Value;
        Hash128 frameAgent = RoleAnchor.Id(RoleIdentityKind.FrameNet, parentA, "Agent")!.Value;

        Assert.Equal(agentA, agentASpaced);
        Assert.NotEqual(agentA, agentALower);
        Assert.NotEqual(agentA, agentB);
        Assert.NotEqual(agentA, frameAgent);
    }

    [Fact]
    public void Emit_DeclaresOneGovernedRole_WithStructuralPhysicality()
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
        PhysicalityRow physicality = Assert.Single(change.Physicalities, p => p.EntityId == role);
        Assert.Equal(PhysicalityType.ParseStructure, physicality.Type);
        // An entity's type is on its row, never recorded as source testimony.
        Assert.DoesNotContain(change.Attestations, a => a.SubjectId == role);
    }
}
