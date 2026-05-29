using System.Text;
using Xunit;
using Laplace.Decomposers.Model;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Token-role mask: the tokenizer framing (leading-space / byte-level) is RECORDED, not
/// stripped, so the canonical-text dedup (▁the / the → "the") stays reversible.
/// </summary>
public class TokenRoleTests
{
    [Theory]
    [InlineData("▁the", TokenRole.LeadingSpace, "the")]  // SentencePiece word-initial
    [InlineData("Ġthe", TokenRole.LeadingSpace, "the")]  // GPT-2 word-initial
    [InlineData("the",  TokenRole.None,         "the")]  // ordinary subword
    [InlineData("cat",  TokenRole.None,         "cat")]
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
        var (cSub,  rSub)  = LlamaTokenizerParser.Canonicalize("the");
        Assert.Equal(cInit, cSub);                        // same content → same entity (dedup)
        Assert.True(rInit.HasFlag(TokenRole.LeadingSpace)); // word-initial form
        Assert.False(rSub.HasFlag(TokenRole.LeadingSpace)); // subword form — distinguishable
    }

    [Theory]
    [InlineData("<0x41>", (byte)0x41)]  // 'A'
    [InlineData("<0x0A>", (byte)0x0A)]  // newline byte
    [InlineData("<0xFF>", (byte)0xFF)]  // invalid-UTF-8 byte
    public void Canonicalize_ByteLevel_Flagged_DecodesByte(string raw, byte expected)
    {
        var (canonical, role) = LlamaTokenizerParser.Canonicalize(raw);
        Assert.Equal(TokenRole.ByteLevel, role);
        Assert.Equal(new[] { expected }, canonical);
    }
}
