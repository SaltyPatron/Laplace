namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// Validates the TokenizerAssetDecomposer surface canonicalization fix
/// (P2.10): tokenizer-specific encoding artifacts (## / Ġ / ▁ / GPT-2
/// byte-fallback) are stripped BEFORE F1, so cross-model dedup actually
/// holds at the substrate entity level.
///
/// Specifically: BERT WordPiece's continuation token "##ing" must
/// canonicalize to "ing" and produce the SAME substrate entity hash as
/// F1.DecomposeAsync("ing") — without canonicalization, F1 would hash the
/// raw "##ing" surface separately, breaking dedup.
/// </summary>
public class TokenizerCanonicalizationTest
{
    private const string MiniLmTokenizerPath =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf\tokenizer.json";

    [Fact]
    public void DetectKind_OnMiniLm_ReturnsWordPiece()
    {
        if (!File.Exists(MiniLmTokenizerPath)) { return; }

        var kind = TokenizerJsonParser.DetectKind(MiniLmTokenizerPath);
        Assert.Equal(TokenizerKind.WordPiece, kind);
    }

    [Fact]
    public void DecodeToCanonical_WordPieceContinuation_StripsHashHashPrefix()
    {
        Assert.Equal("ing", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.WordPiece, "##ing"));
        Assert.Equal("the", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.WordPiece, "the"));
    }

    [Fact]
    public void DecodeToCanonical_ByteLevelBpe_StripsLeadingSpaceMarker()
    {
        // 'Ġ' = U+0120 = byte-level marker for byte 0x20 (space).
        Assert.Equal("the", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.ByteLevelBpe, "Ġthe"));
        Assert.Equal("the", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.ByteLevelBpe, "the"));
    }

    [Fact]
    public void DecodeToCanonical_SentencePiece_StripsLeadingSpaceMarker()
    {
        // '▁' = U+2581 = SentencePiece leading-space marker.
        Assert.Equal("the", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.SentencePieceUnigram, "▁the"));
        Assert.Equal("the", TokenizerSurfaceDecoder.DecodeToCanonical(TokenizerKind.SentencePieceUnigram, "the"));
    }

    [Fact]
    public async Task TokenizerAssetDecomposer_OnMiniLm_DedupesContinuationTokenWithRoot()
    {
        if (!File.Exists(MiniLmTokenizerPath)) { return; }

        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink = new EntityRecorder();
        var childSink  = new EntityChildRecorder();
        var edgeSink   = new EdgeRecorder();
        var provenance = new ProvenanceRecorder(resolver);
        var f1         = new TextDecomposer(pool, hashing, entitySink, childSink);
        var decomposer = new TokenizerAssetDecomposer(f1, hashing, resolver, edgeSink, provenance);

        await decomposer.DecomposeAsync(
            AtomId.FromSpan(new byte[AtomId.SizeBytes]),
            "test_minilm",
            MiniLmTokenizerPath,
            CancellationToken.None);

        // After canonicalization: F1("ing") must appear among emitted token
        // participants. Both the BERT vocab's "ing" entry (if present) and
        // the "##ing" entry now route to F1("ing") → same substrate hash.
        var expectedIngHash = await f1.DecomposeAsync("ing", CancellationToken.None);
        var expectedHex     = System.Convert.ToHexString(expectedIngHash.AsSpan());

        var matchCount = edgeSink.MemberRecords
            .Count(m => System.Convert.ToHexString(m.ParticipantHash.AsSpan()) == expectedHex);

        // BERT vocab has "ing" at one position and "##ing" at another.
        // After canonicalization both produce the same substrate hash, so
        // the participant should appear in MULTIPLE edge_member rows
        // (once for each tokenizer entry whose canonical form is "ing").
        // Pre-canonicalization (the old buggy behavior) "##ing" would have
        // hashed differently, so we'd see only ONE match.
        Assert.True(matchCount >= 2,
            $"expected ≥ 2 token-member rows for canonical 'ing' (root + continuation), got {matchCount}");
    }

    // -----------------------------------------------------------------
    // Inline recorders (matches the pattern in TatoebaDecomposerIntegrationTest
    // / SafetensorsHeaderDecomposerTest / FireflyJarTest). Future cleanup:
    // promote to a shared internal test helper.
    // -----------------------------------------------------------------

    private sealed class EntityRecorder : IEntityEmission
    {
        public List<EntityRecord> Records { get; } = new();
        public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class EntityChildRecorder : IEntityChildEmission
    {
        public List<EntityChildRecord> Records { get; } = new();
        public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class EdgeRecorder : IEdgeEmission
    {
        public List<EdgeRecord>       EdgeRecords   { get; } = new();
        public List<EdgeMemberRecord> MemberRecords { get; } = new();
        public ValueTask EmitEdgeAsync(EdgeRecord record, CancellationToken cancellationToken)
        { EdgeRecords.Add(record); return ValueTask.CompletedTask; }
        public ValueTask EmitMemberAsync(EdgeMemberRecord record, CancellationToken cancellationToken)
        { MemberRecords.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class ProvenanceRecorder : IProvenance
    {
        private readonly ConceptEntityResolver _resolver;
        public ProvenanceRecorder(ConceptEntityResolver resolver) { _resolver = resolver; }
        public List<EntityProvenanceRecord> EntityProvenance { get; } = new();
        public List<EdgeProvenanceRecord>   EdgeProvenance   { get; } = new();
        public Task<AtomId> ResolveSourceAsync(string canonicalName, CancellationToken cancellationToken)
            => Task.FromResult(_resolver.Resolve(canonicalName));
        public ValueTask EmitEntityProvenanceAsync(EntityProvenanceRecord record, CancellationToken cancellationToken)
        { EntityProvenance.Add(record); return ValueTask.CompletedTask; }
        public ValueTask EmitEdgeProvenanceAsync(EdgeProvenanceRecord record, CancellationToken cancellationToken)
        { EdgeProvenance.Add(record); return ValueTask.CompletedTask; }
    }
}
