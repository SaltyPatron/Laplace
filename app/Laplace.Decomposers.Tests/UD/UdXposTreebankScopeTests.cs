using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.UD;

public sealed class UdXposTreebankScopeTests
{
    [Fact]
    public void SameLanguageSameTagDifferentTreebanksHaveDifferentIdentities()
    {
        string ewt = UdSentenceEmitContext.XposIdentityScope(
            "en", "ud/UD_English-EWT/en_ewt-ud-train");
        string gum = UdSentenceEmitContext.XposIdentityScope(
            "en", "ud/UD_English-GUM/en_gum-ud-train");

        Assert.Equal("en/UD_English-EWT", ewt);
        Assert.Equal("en/UD_English-GUM", gum);
        Assert.NotEqual(ewt, gum);

        Hash128 ewtNn = UdParseStructure.XposId(ewt, "NN");
        Hash128 gumNn = UdParseStructure.XposId(gum, "NN");
        Assert.NotEqual(ewtNn, gumNn);
    }

    [Fact]
    public void SameTreebankAcrossSplitsSharesOneTagsetIdentity()
    {
        string train = UdSentenceEmitContext.XposIdentityScope(
            "en", "ud/UD_English-EWT/en_ewt-ud-train");
        string dev = UdSentenceEmitContext.XposIdentityScope(
            "en", "ud/UD_English-EWT/en_ewt-ud-dev");

        Assert.Equal(train, dev);
        Assert.Equal(
            UdParseStructure.XposId(train, "NN"),
            UdParseStructure.XposId(dev, "NN"));
    }

    [Fact]
    public void NonTreebankFixtureRetainsHistoricalLanguageScope()
    {
        string fixture = UdSentenceEmitContext.XposIdentityScope("en", "ud/test.conllu");
        Assert.Equal("en", fixture);
        Assert.Equal(
            UdParseStructure.XposId("en", "NN"),
            UdParseStructure.XposId(fixture, "NN"));
    }
}