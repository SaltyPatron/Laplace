using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

public class FeedbackContentTests
{
    [Fact]
    public void TryResolveRelation_CanonicalUppercase_Resolves()
    {
        Assert.True(FeedbackContent.TryResolveRelation("IS_A", out var isA));
        Assert.Equal(RelationTypeRegistry.RelationTypeId("IS_A"), isA.Id);
        Assert.Equal("IS_A", isA.Canonical);

        Assert.True(FeedbackContent.TryResolveRelation("PRECEDES", out var prec));
        Assert.Equal(RelationTypeRegistry.RelationTypeId("PRECEDES"), prec.Id);
    }

    [Fact]
    public void TryResolveRelation_OrdinaryTokensAndAliases_Rejected()
    {
        // Lowercase words must never be misread as relations (triple-mode guard).
        Assert.False(FeedbackContent.TryResolveRelation("cat", out _));
        Assert.False(FeedbackContent.TryResolveRelation("is_a", out _));
        Assert.False(FeedbackContent.TryResolveRelation("", out _));
        // Non-canonical alias (FOLLOWS flips to PRECEDES) is not a manifest canonical.
        Assert.False(FeedbackContent.TryResolveRelation("FOLLOWS", out _));
    }

    [Fact]
    public void BuildTriple_ConfirmAndRefute_MapToOutcomePolarity()
    {
        var s = Hash128.OfCanonical("substrate/test/feedback/subject");
        var o = Hash128.OfCanonical("substrate/test/feedback/object");

        var confirm = FeedbackContent.BuildTriple(s, "IS_A", o, confirm: true);
        var refute = FeedbackContent.BuildTriple(s, "IS_A", o, confirm: false);

        var ca = Assert.Single(confirm.Attestations);
        var ra = Assert.Single(refute.Attestations);
        Assert.Equal(AttestationOutcome.Confirm, ca.Outcome);
        Assert.Equal(AttestationOutcome.Refute, ra.Outcome);

        // Same triple ⇒ same consensus arena: subject/type/object identical.
        Assert.Equal(RelationTypeRegistry.RelationTypeId("IS_A"), ca.TypeId);
        Assert.Equal(ca.SubjectId, ra.SubjectId);
        Assert.Equal(ca.TypeId, ra.TypeId);
        Assert.Equal(ca.ObjectId, ra.ObjectId);
        Assert.Equal(FeedbackContent.Source, ca.SourceId);
    }

    [Fact]
    public void BuildPrecedesChain_NTokens_NMinusOnePairs()
    {
        var ids = new[]
        {
            Hash128.OfCanonical("substrate/test/feedback/t1"),
            Hash128.OfCanonical("substrate/test/feedback/t2"),
            Hash128.OfCanonical("substrate/test/feedback/t3"),
        };

        var change = FeedbackContent.BuildPrecedesChain(ids, confirm: true);
        Assert.Equal(2, change.Attestations.Length);
        var precedes = RelationTypeRegistry.RelationTypeId("PRECEDES");
        Assert.All(change.Attestations, a => Assert.Equal(precedes, a.TypeId));
        Assert.All(change.Attestations, a => Assert.Equal(AttestationOutcome.Confirm, a.Outcome));
        Assert.Equal(ids[0], change.Attestations[0].SubjectId);
        Assert.Equal(ids[1], change.Attestations[0].ObjectId);
        Assert.Equal(ids[1], change.Attestations[1].SubjectId);
        Assert.Equal(ids[2], change.Attestations[1].ObjectId);
    }

    [Fact]
    public void BuildPrecedesChain_FewerThanTwo_Throws()
    {
        var one = new[] { Hash128.OfCanonical("substrate/test/feedback/only") };
        Assert.Throws<ArgumentException>(() => FeedbackContent.BuildPrecedesChain(one, confirm: true));
    }
}
