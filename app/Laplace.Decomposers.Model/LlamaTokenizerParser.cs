using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
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

        /* Substrate-canonical 4D coord of this token entity (tier-tree root
         * Merkle composition of its codepoint trajectory). Used as the
         * Procrustes target when aligning per-source model embeddings onto
         * substrate frame. NaN-filled when TextDecomposer rejected the token
         * (invalid-UTF-8 byte-level token) — caller MUST check before using. */
        public required double   ContentX       { get; init; }
        public required double   ContentY       { get; init; }
        public required double   ContentZ       { get; init; }
        public required double   ContentM       { get; init; }
        public required bool     HasContentCoord { get; init; }
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

            /* Route through TextDecomposer so token entity IDs come from the
             * substrate's standard tier-tree Merkle composition (R5 — same
             * content => same entity ID regardless of source). Falls back to
             * Hash128.Blake3 only for byte-level tokens whose single byte is
             * not valid UTF-8 (TextDecomposer requires valid UTF-8). */
            Hash128 entityId;
            byte tier;
            double cx = double.NaN, cy = double.NaN, cz = double.NaN, cm = double.NaN;
            bool hasContent = false;
            if (TryDecomposeRoot(canonical, out entityId, out tier, out cx, out cy, out cz, out cm))
            {
                /* tier + 4D coord from tree root — codepoint=0 / grapheme=1 / word=2 / etc. */
                hasContent = true;
            }
            else
            {
                entityId = Hash128.Blake3(canonical);
                tier = (byte)(canonical.Length <= 1 ? 0 : canonical.Length <= 4 ? 1 : 2);
            }

            records.Add(new TokenRecord
            {
                TokenId        = tokenId,
                RawToken       = raw,
                CanonicalBytes = canonical,
                EntityId       = entityId,
                Tier           = tier,
                IsByteLevel    = isByteLevel,
                ContentX       = cx,
                ContentY       = cy,
                ContentZ       = cz,
                ContentM       = cm,
                HasContentCoord = hasContent,
            });
        }

        /* Sort by token_id so _tokens[(int)vocabIndex] == entity for token vocabIndex.
         * The QK scorer returns vocab indices, not JSON property order positions. */
        records.Sort((a, b) => a.TokenId.CompareTo(b.TokenId));

        return records;
    }

    /// <summary>
    /// Run the canonical bytes through TextDecomposer; return the root tier-tree
    /// node's content-addressed ID and tier. Returns false (and leaves outputs
    /// at defaults) if TextDecomposer rejects the input (typically: byte-level
    /// tokens whose single byte is not valid UTF-8).
    /// </summary>
    private static bool TryDecomposeRoot(
        byte[] canonical,
        out Hash128 entityId, out byte tier,
        out double cx, out double cy, out double cz, out double cm)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            /* TextDecomposer returns tree topology only — IDs/coords/Hilbert are
             * UNPOPULATED until HashComposer.Run walks the tree leaf-to-trunk
             * via the perfcache resolver (ADR 0048). */
            unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

            int nc = tree.NodeCount;
            if (nc == 0)
            {
                entityId = default; tier = 0;
                cx = cy = cz = cm = double.NaN;
                return false;
            }
            var rootNode = tree.GetNode((uint)(nc - 1));
            entityId = rootNode.Id;
            tier = rootNode.Tier;
            unsafe { cx = rootNode.Coord[0]; cy = rootNode.Coord[1]; cz = rootNode.Coord[2]; cm = rootNode.Coord[3]; }
            return true;
        }
        catch (InvalidOperationException)
        {
            entityId = default;
            tier = 0;
            cx = cy = cz = cm = double.NaN;
            return false;
        }
    }

    /// <summary>
    /// Run the canonical bytes through TextDecomposer + TextEntityBuilder to
    /// produce entity + CONTENT-physicality rows for the token and every
    /// tier-tree ancestor (codepoints, graphemes). Returns false if
    /// TextDecomposer rejects the input.
    /// </summary>
    private static bool TryBuildTreeRows(
        byte[] canonical, Hash128 sourceId,
        out ImmutableArray<EntityRow> entities,
        out ImmutableArray<PhysicalityRow> physicalities)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            /* TextDecomposer returns tree topology only; HashComposer fills
             * IDs / coords / Hilbert leaf-to-trunk via the perfcache resolver
             * (ADR 0048). TextEntityBuilder REQUIRES this before Build(). */
            unsafe { HashComposer.Run(tree, &PerfcacheResolver); }
            var (es, ps) = new TextEntityBuilder(tree, sourceId).Build();
            entities = es;
            physicalities = ps;
            return true;
        }
        catch (InvalidOperationException)
        {
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            return false;
        }
    }

    /* Perfcache-backed atom resolver for HashComposer (ADR 0048). Mirrors
     * ContentRoundtrip.Resolver — reads codepoint hash/coord/Hilbert from the
     * process-wide T0 perf-cache. Caller MUST have invoked
     * CodepointPerfcache.Load(path) before any decomposer that triggers
     * HashComposer.Run; the failure mode if not loaded is a clean engine
     * error code rather than silent corruption. */
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData,
        Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
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
    /// Yield SubstrateChange batches for all token entities + TOKEN_MAPS_TO attestations.
    /// Records MUST be sorted by TokenId (Parse() does this) so that
    /// _tokens[(int)vocabIndex].EntityId is correct for the QK scorer's vocab-index output.
    ///
    /// TOKEN_MAPS_TO attestation: (tokenizerEntityId → textEntityId, rating = token_id).
    /// Rating encodes the vocabulary position — positional kind by design, not Glicko-2.
    /// Synthesis queries these to recover the token_id → entity mapping.
    ///
    /// tokenizerEntityId MUST already be in the DB before these batches are applied.
    /// </summary>
    public static IEnumerable<SubstrateChange> BuildBatches(
        IReadOnlyList<TokenRecord> records,
        Hash128 sourceId,
        Hash128 textTypeId,
        Hash128 tokenizerEntityId,
        Hash128 tokenMapsToKindId,
        int batchSize = 512)
    {
        int total = records.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int end = Math.Min(start + batchSize, total);
            int n   = end - start;

            /* Capacity ~= 5 nodes per tier tree on average (codepoint + grapheme
             * + word ancestors); DB-level ON CONFLICT DO NOTHING dedupes
             * codepoints that UnicodeDecomposer already emitted. */
            var b = new SubstrateChangeBuilder(
                sourceId,
                $"tokenizer/vocab/{start}..{end - 1}",
                entityCapacity: n * 5,
                physicalityCapacity: n * 5,
                attestationCapacity: n);

            /* Attestation ID buffer reused per token to avoid per-iteration stackalloc. */
            byte[] attBufArr = new byte[80];
            tokenizerEntityId.WriteBytes(attBufArr.AsSpan(0, 16));
            tokenMapsToKindId.WriteBytes(attBufArr.AsSpan(16, 16));
            sourceId.WriteBytes(attBufArr.AsSpan(48, 16));
            /* context = zero, already default-initialized */

            for (int i = start; i < end; i++)
            {
                var rec = records[i];

                /* Substrate-consistent emit: route the token's bytes through
                 * TextDecomposer + TextEntityBuilder so the token entity AND
                 * its codepoint trajectory ancestors get content-addressed
                 * + 4D-positioned the same way every other text entity does.
                 * Falls back to a plain EntityRow only for invalid-UTF-8
                 * byte-level tokens. */
                if (TryBuildTreeRows(rec.CanonicalBytes, sourceId, out var treeEntities, out var treePhys))
                {
                    foreach (var e in treeEntities) b.AddEntity(e);
                    foreach (var p in treePhys)    b.AddPhysicality(p);
                }
                else
                {
                    /* No CONTENT physicality for non-UTF-8 byte-level tokens —
                     * entity exists but lacks a substrate-canonical 4D anchor. */
                    b.AddEntity(rec.EntityId, rec.Tier, textTypeId, firstObservedBy: sourceId);
                }

                /* TOKEN_MAPS_TO: tokenizer entity → text entity, rating = vocab position.
                 * Synthesis uses this to reconstruct the token_id → entity mapping
                 * without needing the original tokenizer.json file. */
                rec.EntityId.WriteBytes(attBufArr.AsSpan(32, 16));
                var attId = Hash128.Blake3(attBufArr);

                b.AddAttestation(new AttestationRow(
                    Id:               attId,
                    SubjectId:        tokenizerEntityId,
                    KindId:           tokenMapsToKindId,
                    ObjectId:         rec.EntityId,
                    SourceId:         sourceId,
                    ContextId:        null,
                    RatingFp1e9:      (long)rec.TokenId,
                    RdFp1e9:          1L,
                    VolatilityFp1e9:  1L,
                    LastObservedAtUnixUs: 0,
                    ObservationCount: 1));
            }

            yield return b.Build();
        }
    }
}
