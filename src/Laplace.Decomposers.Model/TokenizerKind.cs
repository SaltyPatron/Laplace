namespace Laplace.Decomposers.Model;

/// <summary>
/// Family of HuggingFace tokenizer formats. Each family encodes raw text
/// surfaces differently — naive raw-surface F1 dedup across families produces
/// FALSE non-matches. The substrate's cross-model dedup property requires
/// surfaces to be decoded to canonical text BEFORE routing through F1.
///
/// Detected by inspecting tokenizer.json's <c>model.type</c> + the
/// <c>pre_tokenizer</c> / <c>decoder</c> chain (specifically whether the
/// pre-tokenizer or decoder includes a <c>ByteLevel</c> step, which
/// distinguishes byte-level BPE from raw BPE).
/// </summary>
public enum TokenizerKind
{
    Unknown,

    /// <summary>BERT-family: surfaces are raw text; "##" prefix marks
    /// continuation subwords (e.g., "##ing" appended to a previous token).</summary>
    WordPiece,

    /// <summary>GPT-2 / Llama / Qwen / DeepSeek / Mistral byte-level BPE.
    /// Each input byte maps to a printable Unicode codepoint via the GPT-2
    /// <c>bytes_to_unicode</c> table; surfaces use these codepoints not
    /// raw text. Leading-space byte 0x20 → U+0120 ('Ġ').</summary>
    ByteLevelBpe,

    /// <summary>T5/mT5/ALBERT-family: surfaces use SentencePiece's '▁'
    /// (U+2581) as a leading-space marker. "▁the" = " the" canonically.</summary>
    SentencePieceUnigram,

    /// <summary>BPE without byte-level encoding — surfaces are raw text
    /// + merge boundaries. Rare in modern HF models; Whisper variants use it.</summary>
    Bpe,
}
