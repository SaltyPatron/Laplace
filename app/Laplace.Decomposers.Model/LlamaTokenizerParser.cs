using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Parses a HuggingFace tokenizer.json (BPE/SentencePiece format) into
/// substrate Text entity rows.
///
/// BPE marker stripping:
///   '▁' (U+2581 LOWER ONE EIGHTH BLOCK) → leading space stripped
///   'Ġ'  (U+0120 LATIN SMALL LETTER G WITH DOT ABOVE) → leading space stripped (GPT-2)
///   'Ċ'  (U+010A) → newline (stripped for canonical surface)
///   Byte-level tokens '&lt;0xNN&gt;' → single byte 0xNN (canonical bytes = [0xNN])
///
/// Canonical surface bytes = UTF-8 of stripped token string (or raw byte for byte-level).
/// Entity ID = Hash128.Blake3(canonical_bytes), consistent with text corpus entities.
/// </summary>
public sealed class LlamaTokenizerParser
{
    public sealed class TokenRecord
    {
        public required int      TokenId        { get; init; }
        public required string   RawToken       { get; init; }
        public required byte[]   CanonicalBytes { get; init; }
        public required Hash128  EntityId       { get; init; }
        public required byte     Tier           { get; init; }
        public required bool     IsByteLevel    { get; init; }
    }

    /// <summary>
    /// Parse tokenizer.json and return all vocab entries as TokenRecords.
    /// Handles SentencePiece (▁) and GPT-2 (Ġ) BPE markers, byte-level tokens.
    /// </summary>
    public static IReadOnlyList<TokenRecord> Parse(string tokenizerJsonPath)
    {
        byte[] jsonBytes = File.ReadAllBytes(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(jsonBytes);

        /* Navigate to model.vocab — the canonical vocab map */
        JsonElement vocab;
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("vocab", out vocab))
        {
            /* Some tokenizer.json formats put vocab at root level */
            if (!doc.RootElement.TryGetProperty("vocab", out vocab))
                throw new InvalidDataException("tokenizer.json: cannot find model.vocab or vocab");
        }

        var records = new List<TokenRecord>(vocab.GetPropertyCount() + 16);

        foreach (var entry in vocab.EnumerateObject())
        {
            string raw = entry.Name;
            int tokenId = entry.Value.GetInt32();

            (byte[] canonical, bool isByteLevel) = Canonicalize(raw);
            byte tier = (byte)(canonical.Length <= 1 ? 0 : canonical.Length <= 4 ? 1 : 2);
            var entityId = Hash128.Blake3(canonical);

            records.Add(new TokenRecord
            {
                TokenId        = tokenId,
                RawToken       = raw,
                CanonicalBytes = canonical,
                EntityId       = entityId,
                Tier           = tier,
                IsByteLevel    = isByteLevel,
            });
        }

        return records;
    }

    /// <summary>
    /// Strip BPE markers and return canonical byte representation.
    /// </summary>
    public static (byte[] canonical, bool isByteLevel) Canonicalize(string rawToken)
    {
        /* Byte-level token: <0xNN> */
        if (rawToken.Length == 6 && rawToken.StartsWith("<0x", StringComparison.Ordinal)
            && rawToken.EndsWith('>'))
        {
            string hex = rawToken.Substring(3, 2);
            if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                return ([b], true);
        }

        /* Strip leading SentencePiece space marker '▁' (U+2581) */
        string surface = rawToken;
        if (surface.Length > 0 && surface[0] == '▁')
            surface = surface.Substring(1);

        /* Strip leading GPT-2 space marker 'Ġ' (U+0120) */
        if (surface.Length > 0 && surface[0] == 'Ġ')
            surface = surface.Substring(1);

        /* Replace GPT-2 newline marker 'Ċ' (U+010A) with actual newline */
        surface = surface.Replace('Ċ', '\n');

        /* Empty after stripping (e.g. the space token itself "▁") → single space byte */
        if (surface.Length == 0)
            surface = " ";

        return (Encoding.UTF8.GetBytes(surface), false);
    }

    /// <summary>
    /// Yield SubstrateChange batches for all token entities.
    /// </summary>
    public static IEnumerable<SubstrateChange> BuildBatches(
        IReadOnlyList<TokenRecord> records,
        Hash128 sourceId,
        Hash128 textTypeId,
        int batchSize = 512)
    {
        int total = records.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int end = Math.Min(start + batchSize, total);
            int n   = end - start;

            var b = new SubstrateChangeBuilder(
                sourceId,
                $"tokenizer/vocab/{start}..{end - 1}",
                entityCapacity: n, physicalityCapacity: 0, attestationCapacity: 0);

            for (int i = start; i < end; i++)
            {
                var rec = records[i];
                b.AddEntity(rec.EntityId, rec.Tier, textTypeId, firstObservedBy: sourceId);
            }

            yield return b.Build();
        }
    }
}
