namespace Laplace.Smoke.Tests;

using Laplace.Decomposers.Model;

using Xunit;

/// <summary>
/// Verifies TokenizerSurfaceDecoder canonicalizes surfaces from each major
/// HuggingFace tokenizer family to the SAME canonical text. This is the
/// foundation of cross-model dedup at the token level: without it, F1
/// produces a different entity hash for "the" / "Ġthe" / "▁the" / "##the"
/// — and Llama's "the" can never match BERT's "the" in the substrate.
/// </summary>
public class TokenizerSurfaceDecoderTests
{
    [Theory]
    // WordPiece: ## prefix is a continuation marker → strip it.
    [InlineData(TokenizerKind.WordPiece, "the",   "the")]
    [InlineData(TokenizerKind.WordPiece, "##ing", "ing")]
    [InlineData(TokenizerKind.WordPiece, "##s",   "s")]
    [InlineData(TokenizerKind.WordPiece, "[CLS]", "[CLS]")]   // pass-through (special-ish)
    // BPE byte-level (GPT-2/Llama/Qwen): Ġ = leading-space marker → strip.
    [InlineData(TokenizerKind.ByteLevelBpe, "Ġthe", "the")]
    [InlineData(TokenizerKind.ByteLevelBpe, "the",  "the")]
    [InlineData(TokenizerKind.ByteLevelBpe, "Ġcat", "cat")]
    // SentencePiece (T5): ▁ = leading-space marker.
    [InlineData(TokenizerKind.SentencePieceUnigram, "▁the", "the")]
    [InlineData(TokenizerKind.SentencePieceUnigram, "▁cat", "cat")]
    [InlineData(TokenizerKind.SentencePieceUnigram, "the",  "the")]
    public void Decode_ReturnsCanonicalText(TokenizerKind kind, string surface, string expected)
    {
        var actual = TokenizerSurfaceDecoder.DecodeToCanonical(kind, surface);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decode_FourFamilyEquivalence_Of_the_AllProduceCanonicalThe()
    {
        // The cross-family content-addressing claim: "the" prefixed with
        // a leading-space marker in any of the four major tokenizer
        // families decodes to identical canonical text. This is what
        // makes Llama's "the" hash-equal to BERT's "the" in the substrate.
        var canonical = "the";
        Assert.Equal(canonical, TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.WordPiece,            "the"));
        Assert.Equal(canonical, TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.ByteLevelBpe,         "Ġthe"));
        Assert.Equal(canonical, TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.SentencePieceUnigram, "▁the"));
        Assert.Equal(canonical, TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.Bpe,                  "the"));
    }

    [Theory]
    // GPT-2 byte_to_unicode special mappings — verify a few specific bytes
    // get back their original raw bytes through the reverse table.
    // Newline byte 0x0A → U+010A ('Ċ'). Tab 0x09 → U+0109 ('ĉ'). Space
    // 0x20 → U+0120 ('Ġ').
    [InlineData("Ġ",   " ")]    // single leading-space marker → empty after trim
    [InlineData("Ċ",   "\n")]   // newline byte
    public void Decode_ByteLevelBpe_KnownByteMappings(string surface, string expectedRaw)
    {
        // Special case for "Ġ" alone: trim leading space yields empty.
        var actual = TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.ByteLevelBpe, surface);
        var expected = expectedRaw.StartsWith(' ') ? expectedRaw[1..] : expectedRaw;
        Assert.Equal(expected, actual);
    }
}
