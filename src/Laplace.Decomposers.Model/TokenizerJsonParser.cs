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
/// </summary>
public sealed class TokenizerJsonParser
{
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
