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
/// Tokenizer-framing role of a vocab entry — recorded so the canonical-text dedup
/// (▁the / the → "the") stays reversible and special/byte tokens are handled correctly,
/// rather than the framing being stripped and lost. Tokenizer-family-agnostic
/// (SentencePiece ▁, GPT-2/BPE Ġ/Ċ, byte-fallback, added specials).
/// </summary>
[Flags]
public enum TokenRole : byte
{
    None         = 0,   // ordinary subword (no leading-space framing)
    LeadingSpace = 1,   // word-initial: SentencePiece '▁' / GPT-2 'Ġ' marker
    ByteLevel    = 2,   // '<0xNN>' byte-fallback (raw byte content)
    Special      = 4,   // control/special token from the tokenizer's added_tokens
}

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
        /// <summary>Tokenizer-framing role mask (leading-space / byte-level / special).
        /// Recorded into the substrate so the canonical-text dedup is reversible.</summary>
        public required TokenRole Role          { get; init; }

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

        /* Special/control token ids from added_tokens (special:true): <s>, </s>, <unk>,
         * [INST], … These are STRUCTURAL, not their literal characters — flagged Special
         * and given a distinct content-addressed entity, never a text decomposition. */
        var specialIds = new HashSet<int>();
        if (doc.RootElement.TryGetProperty("added_tokens", out var added) &&
            added.ValueKind == JsonValueKind.Array)
        {
            foreach (var at in added.EnumerateArray())
                if (at.TryGetProperty("special", out var sp) && sp.ValueKind == JsonValueKind.True &&
                    at.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int sid))
                    specialIds.Add(sid);
        }

        var records = new List<TokenRecord>(vocab.GetPropertyCount() + 16);

        foreach (var entry in vocab.EnumerateObject())
        {
            string raw = entry.Name;
            int tokenId = entry.Value.GetInt32();

            (byte[] canonical, TokenRole role) = Canonicalize(raw);
            if (specialIds.Contains(tokenId)) role |= TokenRole.Special;

            /* Route through TextDecomposer so token entity IDs come from the
             * substrate's standard tier-tree Merkle composition (R5 — same
             * content => same entity ID). Specials get a distinct structural entity;
             * byte-level / invalid-UTF-8 fall back to a plain Blake3 entity. */
            Hash128 entityId;
            byte tier;
            double cx = double.NaN, cy = double.NaN, cz = double.NaN, cm = double.NaN;
            bool hasContent = false;
            if (role.HasFlag(TokenRole.Special))
            {
                entityId = Hash128.OfCanonical($"substrate/token/special/{raw}/v1");
                tier = 0;
            }
            else if (TryDecomposeRoot(canonical, out entityId, out tier, out cx, out cy, out cz, out cm))
            {
                /* tier + 4D coord from tree root — codepoint=0 / grapheme=1 / word=2 / etc. */
                hasContent = true;
            }
            else
            {
                entityId = Hash128.Blake3(canonical);
                tier = (byte)(canonical.Length <= 1 ? 0 : canonical.Length <= 4 ? 1 : 2);
                if (canonical.Length == 1 && canonical[0] >= ByteAtoms.First)
                {
                    /* BYTE TIER (2026-06-05): a high-byte token IS the byte atom
                     * (same content, same id as the seeded entity) — anchor at
                     * its canonical placement so the morph residual-scores it
                     * like any codepoint. The floor is no longer unanchored. */
                    var bc = ByteAtoms.Coord(canonical[0]);
                    cx = bc[0]; cy = bc[1]; cz = bc[2]; cm = bc[3];
                    hasContent = true;
                }
            }

            records.Add(new TokenRecord
            {
                TokenId        = tokenId,
                RawToken       = raw,
                CanonicalBytes = canonical,
                EntityId       = entityId,
                Tier           = tier,
                IsByteLevel    = role.HasFlag(TokenRole.ByteLevel),
                Role           = role,
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
             * via the perfcache resolver. */
            unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

            int nc = tree.NodeCount;
            if (nc == 0)
            {
                entityId = default; tier = 0;
                cx = cy = cz = cm = double.NaN;
                return false;
            }
            var rootNode = tree.GetNode(tree.NaturalUnitIndex());
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
            /* Empty tree (e.g. a token that canonicalizes to no content, like the
             * bare ▁ space marker) → no tier-tree entity. MUST mirror
             * TryDecomposeRoot's nc==0 guard: there, Parse falls back to a
             * Blake3(canonical) entity id, and BuildBatches must take the SAME
             * fallback (its else branch emits rec.EntityId) instead of emitting an
             * empty row set — otherwise the token's entity is never inserted and a
             * later QK attestation referencing it violates the subject FK. */
            if (tree.NodeCount == 0)
            {
                entities = ImmutableArray<EntityRow>.Empty;
                physicalities = ImmutableArray<PhysicalityRow>.Empty;
                return false;
            }
            /* TextDecomposer returns tree topology only; HashComposer fills
             * IDs / coords / Hilbert leaf-to-trunk via the perfcache resolver
             *. TextEntityBuilder REQUIRES this before Build(). */
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

    /* Perfcache-backed atom resolver for HashComposer. Mirrors
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
    /// <summary>
    /// Canonicalize a raw tokenizer entry to its content bytes AND the token-role mask
    /// the tokenizer framing implies. The role is RECORDED (not discarded): the leading
    /// space marker is what distinguishes the word-initial form ("▁the") from the subword
    /// form ("the") even though both share the canonical text "the" — needed for faithful
    /// re-tokenization on synthesis. <see cref="TokenRole.Special"/> is set by the caller
    /// from the tokenizer's added_tokens metadata, not here.
    /// </summary>
    public static (byte[] canonical, TokenRole role) Canonicalize(string rawToken)
    {
        /* Byte-level token: <0xNN> — raw byte content, flagged. */
        if (rawToken.Length == 6 && rawToken.StartsWith("<0x", StringComparison.Ordinal)
            && rawToken.EndsWith('>'))
        {
            string hex = rawToken.Substring(3, 2);
            if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                return ([b], TokenRole.ByteLevel);
        }

        TokenRole role = TokenRole.None;
        string surface = rawToken;

        /* Leading SentencePiece '▁' (U+2581) or GPT-2 'Ġ' (U+0120) space marker →
         * word-initial; RECORD it as LeadingSpace rather than silently stripping. */
        if (surface.Length > 0 && (surface[0] == '▁' || surface[0] == 'Ġ'))
        {
            role |= TokenRole.LeadingSpace;
            surface = surface.Substring(1);
        }

        /* GPT-2 newline marker 'Ċ' (U+010A) → actual newline. */
        surface = surface.Replace('Ċ', '\n');

        /* Empty after stripping (e.g. the space token itself "▁") → single space byte. */
        if (surface.Length == 0)
            surface = " ";

        return (Encoding.UTF8.GetBytes(surface), role);
    }

    /// <summary>
    /// Yield SubstrateChange batches for all token ENTITIES (+ their tier-tree
    /// content). Records MUST be sorted by TokenId (Parse() does this) so that
    /// _tokens[(int)vocabIndex].EntityId is correct for the QK scorer's
    /// vocab-index output.
    ///
    /// TOKEN_MAPS_TO is NOT emitted here (2026-06-05 ruling): the mapping is a
    /// MAGNITUDE matchup scored by the S³-morph's per-token alignment residual
    /// ("the distance becomes a metric in the Glicko-2 match"), so the
    /// attestations are emitted by <see cref="TokenS3Morph.Emit"/> where the
    /// residuals exist — strictly after these entities land (FK-safe). The
    /// categorical fallback (morph skipped/degenerate) is
    /// <see cref="BuildTokenMapsToCategorical"/>.
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

            /* Capacity ~= 5 nodes per tier tree on average (codepoint + grapheme
             * + word ancestors); DB-level ON CONFLICT DO NOTHING dedupes
             * codepoints that UnicodeDecomposer already emitted. */
            var b = new SubstrateChangeBuilder(
                sourceId,
                $"tokenizer/vocab/{start}..{end - 1}",
                entityCapacity: n * 5,
                physicalityCapacity: n * 5,
                attestationCapacity: 0);

            for (int i = start; i < end; i++)
            {
                var rec = records[i];

                /* Substrate-consistent emit: route the token's bytes through
                 * TextDecomposer + TextEntityBuilder so the token entity AND
                 * its codepoint trajectory ancestors get content-addressed
                 * + 4D-positioned the same way every other text entity does.
                 * Falls back to a plain EntityRow only for invalid-UTF-8
                 * byte-level tokens. */
                /* Special/control tokens are structural — emit their distinct entity
                 * directly (rec.EntityId is the special-namespace id, not a text tree),
                 * never decomposed as literal text. Byte-level / invalid-UTF-8 also fall
                 * to the plain-entity path (TryBuildTreeRows returns false). */
                if (!rec.Role.HasFlag(TokenRole.Special)
                    && TryBuildTreeRows(rec.CanonicalBytes, sourceId, out var treeEntities, out var treePhys))
                {
                    foreach (var e in treeEntities) b.AddEntity(e);
                    foreach (var p in treePhys)    b.AddPhysicality(p);
                }
                else
                {
                    /* No CONTENT physicality for specials / non-UTF-8 byte-level tokens —
                     * entity exists but lacks a substrate-canonical 4D anchor. */
                    b.AddEntity(rec.EntityId, (byte)MetaTier.Meta, textTypeId, firstObservedBy: sourceId);
                }

            }

            yield return b.Build();
        }
    }

    /// <summary>
    /// Parse <c>model.merges</c> — the tokenizer's LEARNED merge lattice
    /// ("a b" pairs, ordered by rank; earlier = more fundamental), previously
    /// unextracted (2026-06-05 completeness). Pair parts canonicalize through
    /// the SAME rule as vocab entries.
    /// </summary>
    public static List<(byte[] Left, byte[] Right)> ParseMerges(string tokenizerJsonPath)
    {
        var merges = new List<(byte[], byte[])>();
        byte[] jsonBytes = File.ReadAllBytes(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(jsonBytes);
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("merges", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return merges;
        foreach (var el in arr.EnumerateArray())
        {
            string? pair = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if (string.IsNullOrEmpty(pair)) continue;
            int sp = pair!.IndexOf(' ');
            if (sp <= 0 || sp + 1 >= pair.Length) continue;
            (byte[] l, _) = Canonicalize(pair[..sp]);
            (byte[] r, _) = Canonicalize(pair[(sp + 1)..]);
            merges.Add((l, r));
        }
        return merges;
    }

    /// <summary>
    /// MERGES_WITH matchups from the ordered merge lattice: magnitude
    /// <c>m = M − rank</c> with <c>M = RMS(rank)</c> over the list — earlier
    /// merges (more fundamental) win, the typical rank draws, late ranks lose;
    /// MEASURED from the list, never a knob (the same shape as the
    /// TOKEN_MAPS_TO residual scoring). Self-contained batches: both pair
    /// sides' content entities ride the intent (idempotent with vocab).
    /// </summary>
    public static IEnumerable<SubstrateChange> BuildMergesBatches(
        List<(byte[] Left, byte[] Right)> merges,
        Hash128 sourceId,
        Hash128 textTypeId,
        int batchSize = 8192)
    {
        if (merges.Count == 0) yield break;
        double sumSq = 0;
        for (int i = 0; i < merges.Count; i++) sumSq += (double)i * i;
        double m = Math.Sqrt(sumSq / merges.Count);   // RMS of the rank positions
        if (m <= 0) yield break;

        for (int start = 0; start < merges.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, merges.Count);
            var b = new SubstrateChangeBuilder(
                sourceId, $"tokenizer/merges/{start}..{end - 1}",
                entityCapacity: (end - start) * 6,
                physicalityCapacity: (end - start) * 6,
                attestationCapacity: end - start);
            for (int i = start; i < end; i++)
            {
                var (l, r) = merges[i];
                Hash128 lid = ResolveMergeSide(b, l, sourceId, textTypeId);
                Hash128 rid = ResolveMergeSide(b, r, sourceId, textTypeId);
                b.AddAttestation(Laplace.Decomposers.Abstractions.RelationTypeRegistry.AttestWeighted(
                    lid, "MERGES_WITH", rid, sourceId,
                    Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe,
                    magnitude: m - i, arenaScale: m));
            }
            yield return b.Build();
        }
    }

    /// <summary>One merge-pair side → its content entity, self-contained in the
    /// batch (tier-tree rows when decomposable, Blake3 fallback otherwise —
    /// the EXACT vocab-entry rule, so merge sides converge with token entities).</summary>
    private static Hash128 ResolveMergeSide(
        SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId, Hash128 textTypeId)
    {
        if (TryBuildTreeRows(canonical, sourceId, out var entities, out var physicalities))
        {
            foreach (var e in entities) b.AddEntity(e);
            foreach (var ph in physicalities) b.AddPhysicality(ph);
            if (TryDecomposeRoot(canonical, out var rootId, out _, out _, out _, out _, out _))
                return rootId;
        }
        var id = Hash128.Blake3(canonical);
        b.AddEntity(id, (byte)MetaTier.Meta, textTypeId, firstObservedBy: sourceId);
        return id;
    }

    /// <summary>
    /// Categorical TOKEN_MAPS_TO fallback — used ONLY when the S³-morph is
    /// skipped (<c>LAPLACE_SKIP_MORPH=1</c>) or degenerate (no anchored tokens /
    /// kernel failure), so the mapping arena is never empty. Routed through the
    /// registry (rank × source trust → φ — never a hand-coded constant). When
    /// the morph runs, it emits the magnitude-scored form instead
    /// (<see cref="TokenS3Morph.Emit"/>).
    /// </summary>
    public static IEnumerable<SubstrateChange> BuildTokenMapsToCategorical(
        IReadOnlyList<TokenRecord> records,
        Hash128 sourceId,
        Hash128 tokenizerEntityId,
        int batchSize = 8192)
    {
        int total = records.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int end = Math.Min(start + batchSize, total);
            var b = new SubstrateChangeBuilder(
                sourceId, $"tokenizer/maps-to/{start}..{end - 1}",
                entityCapacity: 0, physicalityCapacity: 0,
                attestationCapacity: end - start);
            for (int i = start; i < end; i++)
            {
                b.AddAttestation(Laplace.Decomposers.Abstractions.RelationTypeRegistry.Attest(
                    tokenizerEntityId, "TOKEN_MAPS_TO", records[i].EntityId, sourceId,
                    Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe));
            }
            yield return b.Build();
        }
    }
}
