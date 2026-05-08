namespace Laplace.Decomposers.Model;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Decodes a tokenizer surface string back to its canonical text — the form
/// the substrate uses for content addressing. Different tokenizer families
/// encode the same raw text differently (WordPiece "##the" vs BPE byte-
/// level "Ġthe" vs SentencePiece "▁the"); without this decoder, F1 produces
/// FOUR different entity hashes for the same word, killing cross-model
/// dedup at the foundation. With this decoder, all four families collapse
/// to the same canonical "the" → same F1 hash → same substrate entity.
///
/// Per CLAUDE.md feedback "HF tokenizer format diversity": cross-model dedup
/// at the token level only holds if surfaces normalize to canonical text
/// BEFORE F1.
/// </summary>
public static class TokenizerSurfaceDecoder
{
    private static readonly Dictionary<char, byte> ByteLevelReverseMap = BuildByteLevelReverseMap();

    /// <summary>
    /// Decode <paramref name="surface"/> to canonical text per the
    /// supplied <paramref name="kind"/>. The result is what should route
    /// through F1 TextDecomposer — the same canonical text from any
    /// tokenizer family produces the same substrate entity hash.
    ///
    /// Special tokens (BOS / EOS / PAD / UNK / MASK / "&lt;|im_start|&gt;"
    /// etc.) are NOT decoded — caller is expected to pass IsSpecial=true
    /// surfaces straight through to a model-private entity emission path,
    /// not through this canonicalizer.
    /// </summary>
    public static string DecodeToCanonical(TokenizerKind kind, string surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return kind switch
        {
            TokenizerKind.WordPiece            => DecodeWordPiece(surface),
            TokenizerKind.ByteLevelBpe         => DecodeByteLevelBpe(surface),
            TokenizerKind.SentencePieceUnigram => DecodeSentencePiece(surface),
            TokenizerKind.Bpe                  => surface,
            TokenizerKind.Unknown              => surface,
            _                                  => surface,
        };
    }

    /// <summary>
    /// WordPiece: continuation subwords carry a "##" prefix that means
    /// "this token continues the previous word with no leading space".
    /// Strip the "##" so "##ing" → "ing" canonically. Other surfaces pass
    /// through unchanged.
    /// </summary>
    private static string DecodeWordPiece(string surface)
    {
        return surface.StartsWith("##", StringComparison.Ordinal) ? surface[2..] : surface;
    }

    /// <summary>
    /// GPT-2 byte-level BPE: each surface character is one of 256 printable
    /// Unicode codepoints in the GPT-2 bytes_to_unicode table. Reverse the
    /// table per-char to recover the underlying byte sequence, then UTF-8
    /// decode. The leading-space marker 'Ġ' (U+0120) → byte 0x20 → space —
    /// we trim that single leading space so canonical "the" matches WordPiece's
    /// "the".
    /// </summary>
    private static string DecodeByteLevelBpe(string surface)
    {
        var bytes = new List<byte>(surface.Length * 2);
        foreach (var c in surface)
        {
            if (ByteLevelReverseMap.TryGetValue(c, out var b))
            {
                bytes.Add(b);
            }
            else
            {
                // Codepoint outside the byte-level table — fall through as
                // its UTF-8 bytes. Rare; emerges for surfaces with truly
                // non-ASCII content or special tokens that slipped past
                // the IsSpecial filter.
                foreach (var ub in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    bytes.Add(ub);
                }
            }
        }
        var canonical = Encoding.UTF8.GetString(bytes.ToArray());
        // Trim leading space if it came from a Ġ marker — it's a tokenizer
        // artifact, not part of the lexical content.
        return canonical.StartsWith(' ') ? canonical[1..] : canonical;
    }

    /// <summary>
    /// SentencePiece: '▁' (U+2581) is the leading-space marker. "▁the" →
    /// " the" → "the" after trimming the leading-space artifact (same
    /// rationale as ByteLevelBpe).
    /// </summary>
    private static string DecodeSentencePiece(string surface)
    {
        var replaced = surface.Replace('▁', ' ');
        return replaced.StartsWith(' ') ? replaced[1..] : replaced;
    }

    /// <summary>
    /// Build the GPT-2 bytes_to_unicode reverse table — surface char →
    /// underlying byte. Exact same construction as HuggingFace's
    /// transformers.tokenization_gpt2.bytes_to_unicode().
    /// </summary>
    private static Dictionary<char, byte> BuildByteLevelReverseMap()
    {
        var bs = new List<int>();
        for (var b = (int)'!'; b <= '~';   b++) { bs.Add(b); }
        for (var b = 0xA1;     b <= 0xAC; b++) { bs.Add(b); }
        for (var b = 0xAE;     b <= 0xFF; b++) { bs.Add(b); }

        var cs = new List<int>(bs);
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }

        var rev = new Dictionary<char, byte>(bs.Count);
        for (var i = 0; i < bs.Count; i++)
        {
            rev[(char)cs[i]] = (byte)bs[i];
        }
        return rev;
    }
}
