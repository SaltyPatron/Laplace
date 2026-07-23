using System.Text;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Spec 34 pins: tenant → source identity, session → context entity (chess-game
/// parity), turn-level attestations only, tenant trust as the third witness-weight
/// factor. Id shapes are asserted against the canonical-key law so a drifted key
/// string fails loudly instead of minting a different entity forever.
/// </summary>
public class ConversationContentIdTests
{
    [Fact]
    public void SessionId_IsCanonicalKeyWithTenantInside()
    {
        var id = ConversationContent.SessionId("acme", "sess-1");
        Assert.Equal(Hash128.OfCanonical(SubstrateCanonicalKeys.ConversationSession("acme", "sess-1")), id);
        // Tenant is part of the key: the same client key can never resolve into
        // another tenant's session.
        Assert.NotEqual(ConversationContent.SessionId("rival", "sess-1"), id);
    }

    [Fact]
    public void TenantScope_MintsPerTenantSources_DistinctFromBaseAndEachOther()
    {
        var acme = ConversationContent.Resolve("acme");
        Assert.Equal(SubstrateCanonicalIds.Source("UserPrompt@acme"), acme.PromptSource);
        Assert.Equal(SubstrateCanonicalIds.Source("Response@acme"), acme.ResponseSource);
        Assert.Equal("UserPrompt@acme", acme.PromptSourceName);

        var rival = ConversationContent.Resolve("rival");
        Assert.NotEqual(acme.PromptSource, rival.PromptSource);
        // CLI's bare no-tenant sources remain distinct identities.
        Assert.NotEqual(UserPromptContent.Source, acme.PromptSource);
        Assert.NotEqual(ResponseContent.Source, acme.ResponseSource);
    }

    [Theory]
    [InlineData("local-dev")]
    [InlineData("t.1_x@y-2")]
    [InlineData("A")]
    public void IsValidIdentifier_AcceptsHygienicNames(string value) =>
        Assert.True(ConversationContent.IsValidIdentifier(value));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("naïve")]
    public void IsValidIdentifier_RejectsUnhygienicNames(string? value) =>
        Assert.False(ConversationContent.IsValidIdentifier(value));

    [Fact]
    public void IsValidIdentifier_RejectsOverlongNames() =>
        Assert.False(ConversationContent.IsValidIdentifier(new string('a', 129)));

    [Fact]
    public void Resolve_RejectsInvalidTenantAndTrust()
    {
        Assert.Throws<ArgumentException>(() => ConversationContent.Resolve("bad/tenant"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversationContent.Resolve("acme", 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversationContent.Resolve("acme", -0.1));
    }

    [Fact]
    public void BuildTenantBootstrapChanges_RegistersBothSourcesAndAttribution()
    {
        var scope = ConversationContent.Resolve("acme");
        var changes = ConversationContent.BuildTenantBootstrapChanges(scope);
        Assert.Equal(3, changes.Length);

        var trustClassType = EntityTypeRegistry.Id("HAS_TRUST_CLASS");
        Assert.Contains(changes[0].Attestations, a =>
            a.TypeId == trustClassType && a.SubjectId == scope.PromptSource);
        Assert.Contains(changes[1].Attestations, a =>
            a.TypeId == trustClassType && a.SubjectId == scope.ResponseSource);

        // The declared-relations law: emitted relation families are registered.
        var relationMeta = EntityTypeRegistry.Id("RelationType");
        foreach (var rel in new[] { "APPEARS_IN", "PRECEDES", "HAS_ATTRIBUTION" })
        {
            var relId = RelationTypeRegistry.RelationTypeId(rel);
            Assert.Contains(changes[0].Entities, e => e.Id == relId && e.TypeId == relationMeta);
        }

        var attribution = EntityTypeRegistry.Id("HAS_ATTRIBUTION");
        var subjects = changes[2].Attestations
            .Where(a => a.TypeId == attribution).Select(a => a.SubjectId).ToArray();
        Assert.Contains(scope.PromptSource, subjects);
        Assert.Contains(scope.ResponseSource, subjects);
    }
}

[Collection("GrammarPerfcache")]
public class ConversationContentTurnTests
{
    private static readonly byte[] Prompt = Encoding.UTF8.GetBytes("what does dog mean");
    private static readonly byte[] Reply = Encoding.UTF8.GetBytes("a dog is a domesticated canine");

    private static SubstrateChange BuildTurn(
        string tenant, string sessionKey, double tenantTrust = 1.0,
        byte[]? reply = null, string? userKey = null,
        bool expectReply = true)
    {
        var scope = ConversationContent.Resolve(tenant, tenantTrust);
        var session = ConversationContent.SessionId(tenant, sessionKey);
        Assert.True(ConversationContent.TryBuildTurnChange(
            scope, session, Prompt, reply ?? Reply, userKey,
            out var change, out var promptRoot, out var replyRoot));
        Assert.NotEqual(Hash128.Zero, promptRoot);
        if (expectReply) Assert.NotEqual(Hash128.Zero, replyRoot);
        return change;
    }

    [Fact]
    public void Turn_EmitsSessionEntityAndTurnLevelAttestationsOnly()
    {
        var change = BuildTurn("acme", "s1");
        var session = ConversationContent.SessionId("acme", "s1");

        var sessionRow = Assert.Single(change.Entities, e => e.Id == session);
        Assert.Equal(ConversationContent.SessionType, sessionRow.TypeId);
        Assert.Equal((byte)EntityTier.Document, sessionRow.Tier);

        // ≤4 rows per turn — the re-witness grind stays deleted. Here: 2×APPEARS_IN
        // + 1×PRECEDES, every one context-stamped with the session (chess parity).
        Assert.Equal(3, change.Attestations.Length);
        var appearsIn = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
        var precedes = RelationTypeRegistry.RelationTypeId("PRECEDES");
        Assert.Equal(2, change.Attestations.Count(a => a.TypeId == appearsIn));
        Assert.Equal(1, change.Attestations.Count(a => a.TypeId == precedes));
        Assert.All(change.Attestations, a => Assert.Equal(session, a.ContextId));
        Assert.All(change.Attestations, a => Assert.Equal(AttestationOutcome.Confirm, a.Outcome));

        // Provenance split: prompt membership witnessed by the prompt source,
        // reply membership and the continuation by the response source.
        var scope = ConversationContent.Resolve("acme");
        var pre = Assert.Single(change.Attestations, a => a.TypeId == precedes);
        Assert.Equal(scope.ResponseSource, pre.SourceId);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == appearsIn && a.SourceId == scope.PromptSource);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == appearsIn && a.SourceId == scope.ResponseSource);
    }

    [Fact]
    public void Turn_PromptOnly_NoPrecedesNoReplyMembership()
    {
        var scope = ConversationContent.Resolve("acme");
        var session = ConversationContent.SessionId("acme", "s1");
        Assert.True(ConversationContent.TryBuildTurnChange(
            scope, session, Prompt, null, null, out var change, out _, out var replyRoot));
        Assert.Equal(Hash128.Zero, replyRoot);
        var row = Assert.Single(change.Attestations);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("APPEARS_IN"), row.TypeId);
    }

    [Fact]
    public void Turn_DistinctTenants_DistinctEvidenceRows_SameTenantStable()
    {
        // Provenance is never mashed: the same exchange under two tenants mints
        // distinct attestation ids (source differs), while the same tenant re-
        // asserting is the SAME row identity (idempotent row, repeated testimony).
        var a1 = BuildTurn("acme", "s1").Attestations.Select(x => x.Id).OrderBy(x => x.ToString()).ToArray();
        var a2 = BuildTurn("acme", "s1").Attestations.Select(x => x.Id).OrderBy(x => x.ToString()).ToArray();
        var b1 = BuildTurn("rival", "s1").Attestations.Select(x => x.Id).OrderBy(x => x.ToString()).ToArray();
        Assert.Equal(a1, a2);
        Assert.Empty(a1.Intersect(b1));
    }

    [Fact]
    public void Turn_TenantTrust_LowersWeightRaisesOpponentRd()
    {
        // rank × source trust × TENANT trust → witness_phi: less trusted tenant ⇒
        // higher opponent RD on every emitted row (trust is inside the rating math).
        var full = BuildTurn("acme", "s1", tenantTrust: 1.0).Attestations;
        var half = BuildTurn("acme", "s1", tenantTrust: 0.5).Attestations;
        Assert.Equal(full.Length, half.Length);
        for (int i = 0; i < full.Length; i++)
        {
            Assert.Equal(full[i].Id, half[i].Id); // identity unchanged — trust is not identity
            Assert.True(half[i].OpponentRdFp1e9 > full[i].OpponentRdFp1e9,
                $"row {i}: expected φ({half[i].OpponentRdFp1e9}) > φ({full[i].OpponentRdFp1e9})");
        }
    }

    [Fact]
    public void Turn_UserKey_AttributesSessionOncePerCall()
    {
        var change = BuildTurn("acme", "s1", userKey: "user-7");
        var session = ConversationContent.SessionId("acme", "s1");
        var attribution = RelationTypeRegistry.RelationTypeId("HAS_ATTRIBUTION");
        var row = Assert.Single(change.Attestations, a => a.TypeId == attribution);
        Assert.Equal(session, row.SubjectId);
        Assert.Null(row.ContextId);
        Assert.Equal(4, change.Attestations.Length);
    }

    [Fact]
    public void Turn_InvalidUserKey_Throws()
    {
        var scope = ConversationContent.Resolve("acme");
        var session = ConversationContent.SessionId("acme", "s1");
        Assert.Throws<ArgumentException>(() => ConversationContent.TryBuildTurnChange(
            scope, session, Prompt, Reply, "bad user", out _, out _, out _));
    }
}
