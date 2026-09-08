using System.Text;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Spec 34 pins: tenant → source identity, session → context entity (chess-game
/// parity), occurrence-level testimony, and source-class priors. Id shapes are asserted against the canonical-key law so a drifted key
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
    public void Resolve_RejectsInvalidTenant()
    {
        Assert.Throws<ArgumentException>(() => ConversationContent.Resolve("bad/tenant"));
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
        foreach (var rel in new[] { "APPEARS_IN", "HAS_ATTRIBUTION", "HAS_ROLE", "IS_INSTANCE_OF", "DEPENDS_ON" })
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

    private static (SubstrateChange Change, Hash128 Prompt, Hash128 Reply, Hash128[] Messages) BuildTurn(
        string tenant = "acme", string occurrence = "request-1", string? user = null,
        ConversationContent.TurnPhase phase = ConversationContent.TurnPhase.Complete)
    {
        var scope = ConversationContent.Resolve(tenant);
        var session = ConversationContent.SessionId(tenant, "s1");
        Assert.True(ConversationContent.TryBuildTurnChange(
            scope, session, Prompt, phase == ConversationContent.TurnPhase.Input ? null : Reply, user,
            out var change, out var promptRoot, out var replyRoot, out var messages,
            occurrence, user, phase));
        return (change, promptRoot, replyRoot, messages);
    }

    [Fact]
    public void Turn_OccurrencesPreserveExactContentAndResponseDependency()
    {
        var turn = BuildTurn();
        var session = ConversationContent.SessionId("acme", "s1");
        Assert.Equal(2, turn.Messages.Length);
        Assert.Contains(turn.Change.Entities, e => e.Id == session && e.TypeId == ConversationContent.SessionType);
        Hash128[] contents = [turn.Prompt, turn.Reply];
        for (int i = 0; i < turn.Messages.Length; ++i)
        {
            Hash128 message = turn.Messages[i];
            Assert.NotEqual(contents[i], message);
            Assert.Contains(turn.Change.Entities, e => e.Id == message && e.TypeId == EntityTypeRegistry.ConversationMessage);
            var placement = Assert.Single(turn.Change.Physicalities, p => p.EntityId == message);
            var members = Trajectory.Constituents(placement.TrajectoryXyzm!);
            Assert.Equal(2, members.Length);
            Assert.Equal(contents[i], members[1]);
        }
        var membership = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
        Assert.Equal(turn.Messages.OrderBy(id => id.ToString()), turn.Change.Attestations
            .Where(a => a.TypeId == membership).Select(a => a.SubjectId).OrderBy(id => id.ToString()));
        var dependency = Assert.Single(turn.Change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("DEPENDS_ON"));
        Assert.Equal(turn.Messages[1], dependency.SubjectId);
        Assert.Equal(turn.Messages[0], dependency.ObjectId);
        Assert.Equal(ConversationContent.Resolve("acme").ResponseSource, dependency.SourceId);
        Assert.All(turn.Change.Attestations, a => Assert.Equal(session, a.ContextId));
        Assert.DoesNotContain(turn.Change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("PRECEDES"));
    }

    [Fact]
    public void Turn_RetryKeepsOccurrenceIdentity_RepetitionGetsANewOccurrence()
    {
        var first = BuildTurn();
        var retry = BuildTurn();
        var repeated = BuildTurn(occurrence: "request-2");
        Assert.Equal(first.Messages, retry.Messages);
        Assert.Equal(first.Change.Attestations.Select(a => a.Id), retry.Change.Attestations.Select(a => a.Id));
        Assert.Empty(first.Messages.Intersect(repeated.Messages));
        Assert.Equal(first.Prompt, repeated.Prompt);
        Assert.Equal(first.Reply, repeated.Reply);
    }

    [Fact]
    public void Turn_SplitAdmissionDoesNotRewitnessInputAtResponseCommit()
    {
        var input = BuildTurn(user: "user-7", phase: ConversationContent.TurnPhase.Input);
        var intervening = BuildTurn(occurrence: "request-2", user: "user-7");
        var output = BuildTurn(user: "user-7", phase: ConversationContent.TurnPhase.Output);
        var complete = BuildTurn(user: "user-7");
        Hash128 promptMessage = Assert.Single(input.Messages);
        Hash128 responseMessage = Assert.Single(output.Messages);
        Assert.Equal(complete.Messages, new[] { promptMessage, responseMessage });
        Assert.DoesNotContain(promptMessage, intervening.Messages);
        Assert.DoesNotContain(input.Change.Attestations,
            a => a.SourceId == ConversationContent.Resolve("acme").ResponseSource);
        Assert.All(output.Change.Attestations,
            a => Assert.Equal(ConversationContent.Resolve("acme").ResponseSource, a.SourceId));
        var dependency = Assert.Single(output.Change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("DEPENDS_ON"));
        Assert.Equal(promptMessage, dependency.ObjectId);
        Assert.Equal(responseMessage, dependency.SubjectId);
    }

    [Fact]
    public void Turn_TenantChangesProvenanceWithoutChangingTheSourcePriorOrContent()
    {
        var first = BuildTurn("acme");
        var other = BuildTurn("rival");
        Assert.Equal(first.Prompt, other.Prompt);
        Assert.Equal(first.Reply, other.Reply);
        Assert.Empty(first.Messages.Intersect(other.Messages));
        Assert.Empty(first.Change.Attestations.Select(a => a.Id).Intersect(other.Change.Attestations.Select(a => a.Id)));
        Assert.Equal(first.Change.Attestations.Select(a => a.OpponentRdFp1e9).Order(),
            other.Change.Attestations.Select(a => a.OpponentRdFp1e9).Order());
    }

    [Fact]
    public void Turn_UserAttributionBelongsToSessionAndItsInputOccurrence()
    {
        var turn = BuildTurn(user: "user-7");
        var session = ConversationContent.SessionId("acme", "s1");
        var attribution = RelationTypeRegistry.RelationTypeId("HAS_ATTRIBUTION");
        var rows = turn.Change.Attestations.Where(a => a.TypeId == attribution).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, a => a.SubjectId == session && a.ContextId is null);
        Assert.Contains(rows, a => a.SubjectId == turn.Messages[0] && a.ContextId == session);
        Assert.DoesNotContain(rows, a => a.SubjectId == turn.Messages[1]);
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
