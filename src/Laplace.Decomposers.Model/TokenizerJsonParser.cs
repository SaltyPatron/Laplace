namespace Laplace.Decomposers.Model;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Minimal HuggingFace-tokenizer-asset parser. Handles two shapes:
///   1) tokenizer.json (fast tokenizer format): top-level object with
///      "model.vocab" : {token_str: id, ...} plus "added_tokens" : [{id, content, ...}]
///   2) vocab.json (older, slow tokenizer format): top-level object
///      mapping {token_str: id, ...} directly.
///
/// Yields one <see cref="TokenizerVocabRecord"/> per vocab entry. Special
/// tokens (BOS, EOS, PAD, UNK, MASK, etc.) marked with IsSpecial=true so
/// the F5 decomposer can attach the appropriate substrate flags.
///
/// Also detects <see cref="TokenizerKind"/> (WordPiece / ByteLevelBpe /
/// SentencePieceUnigram / Bpe) from <c>model.type</c> + the pre_tokenizer
/// chain — needed by F5 surface canonicalization (per the
/// HF-tokenizer-format-diversity feedback memory) so cross-model dedup
/// holds: "##ing" (BERT) and "ing" (BERT) and "▁ing" (T5) and "Ġing"
/// (Llama BPE) all canonicalize to "ing" → same substrate entity hash.
/// </summary>
public sealed class TokenizerJsonParser
{
    /// <summary>
    /// Detect the tokenizer family from <paramref name="path"/>'s JSON
    /// header. Reads <c>model.type</c> first, then inspects the
    /// <c>pre_tokenizer</c> chain to discriminate ByteLevelBpe from raw Bpe.
    /// </summary>
    public static TokenizerKind DetectKind(string path)
    {
        using var fs       = File.OpenRead(path);
        using var document = JsonDocument.Parse(fs);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) { return TokenizerKind.Unknown; }
        if (!root.TryGetProperty("model", out var model)) { return TokenizerKind.Unknown; }
        if (!model.TryGetProperty("type",  out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return TokenizerKind.Unknown;
        }

        var modelType = typeEl.GetString();
        return modelType switch
        {
            "WordPiece" => TokenizerKind.WordPiece,
            "Unigram"   => TokenizerKind.SentencePieceUnigram,
            "BPE"       => HasByteLevelStep(root) ? TokenizerKind.ByteLevelBpe : TokenizerKind.Bpe,
            "WordLevel" => TokenizerKind.Unknown,
            _           => TokenizerKind.Unknown,
        };
    }

    private static bool HasByteLevelStep(JsonElement root)
    {
        // pre_tokenizer / decoder may be a single object {"type": "ByteLevel", ...}
        // or a Sequence {"type": "Sequence", "pretokenizers": [{"type": "ByteLevel"}]}.
        return ContainsByteLevel(root, "pre_tokenizer")
            || ContainsByteLevel(root, "decoder");
    }

    private static bool ContainsByteLevel(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var node)) { return false; }
        return ContainsByteLevelRecursive(node);
    }

    private static bool ContainsByteLevelRecursive(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "ByteLevel")
                {
                    return true;
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (ContainsByteLevelRecursive(prop.Value)) { return true; }
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (ContainsByteLevelRecursive(item)) { return true; }
                }
                return false;
            default:
                return false;
        }
    }

    public static IReadOnlyList<TokenizerVocabRecord> Parse(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = new List<TokenizerVocabRecord>();

        // Shape 1: tokenizer.json
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("model", out var model))
        {
            if (model.TryGetProperty("vocab", out var vocab) && vocab.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in vocab.EnumerateObject())
                {
                    entries.Add(new TokenizerVocabRecord(
                        TokenId:   prop.Value.GetInt32(),
                        Surface:   prop.Name,
                        IsSpecial: false));
                }
            }
            if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in added.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var idEl)) { continue; }
                    if (!entry.TryGetProperty("content", out var contentEl)) { continue; }
                    var isSpecial = entry.TryGetProperty("special", out var specialEl) &&
                                    specialEl.ValueKind == JsonValueKind.True;
                    entries.Add(new TokenizerVocabRecord(
                        TokenId:   idEl.GetInt32(),
                        Surface:   contentEl.GetString() ?? string.Empty,
                        IsSpecial: isSpecial));
                }
            }
            return entries;
        }

        // Shape 2: vocab.json (flat mapping)
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number) { continue; }
                entries.Add(new TokenizerVocabRecord(
                    TokenId:   prop.Value.GetInt32(),
                    Surface:   prop.Name,
                    IsSpecial: false));
            }
        }
        return entries;
    }
}
