using System.Text;
using Xunit;
using Laplace.Decomposers.Model;

namespace Laplace.Decomposers.Model.Tests;

public class TokenRoleTests
{
    [Theory]
    [InlineData("▁the", TokenRole.LeadingSpace, "the")]
    [InlineData("Ġthe", TokenRole.LeadingSpace, "the")]
    [InlineData("the", TokenRole.None, "the")]
    [InlineData("cat", TokenRole.None, "cat")]
    public void Canonicalize_RecordsLeadingSpaceRole(string raw, TokenRole expectedRole, string expectedText)
    {
        var (canonical, role) = LlamaTokenizerParser.Canonicalize(raw);
        Assert.Equal(expectedRole, role);
        Assert.Equal(Encoding.UTF8.GetBytes(expectedText), canonical);
    }

    [Fact]
    public void WordInitialAndSubword_ShareCanonicalText_ButDifferByRole()
    {
        var (cInit, rInit) = LlamaTokenizerParser.Canonicalize("▁the");
        var (cSub, rSub) = LlamaTokenizerParser.Canonicalize("the");
        Assert.Equal(cInit, cSub);
        Assert.True(rInit.HasFlag(TokenRole.LeadingSpace));
        Assert.False(rSub.HasFlag(TokenRole.LeadingSpace));
    }

    [Theory]
    [InlineData("<0x41>", (byte)0x41)]
    [InlineData("<0x0A>", (byte)0x0A)]
    [InlineData("<0xFF>", (byte)0xFF)]
    public void Canonicalize_ByteLevel_Flagged_DecodesByte(string raw, byte expected)
    {
        var (canonical, role) = LlamaTokenizerParser.Canonicalize(raw);
        Assert.Equal(TokenRole.ByteLevel, role);
        Assert.Equal(new[] { expected }, canonical);
    }
}
