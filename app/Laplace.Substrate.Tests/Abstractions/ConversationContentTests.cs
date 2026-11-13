using System.Text;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Conversational provenance: tenant → source identity, session → context entity (the
/// same shape as a game context), occurrence-level testimony, and source-class priors.
/// Ids are asserted against the canonical-key law, so a changed key string fails here.
/// </summary>
public class ConversationContentIdTests
{
    [Fact]
    public void SessionId_IsCanonicalKeyWithTenantInside()
    {
        var id = ConversationContent.SessionId("acme", "sess-1");
        Assert.Equal(Hash128.Text(SubstrateCanonicalKeys.ConversationSession("acme", "sess-1")), id);
        // Tenant is part of the key: the same client key can never resolve into
        // another tenant's session.
        Assert.NotEqual(ConversationContent.SessionId("rival", "sess-1"), id);
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
}
