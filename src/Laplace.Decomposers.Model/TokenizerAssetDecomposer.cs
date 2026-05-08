namespace Laplace.Decomposers.Model;

using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F5 — TokenizerAssetDecomposer. The first piece of the AI model
/// decomposer family that exercises cross-model dedup automatically.
///
/// Algorithm per vocab entry (token_id, surface_string):
///   1. Decompose surface_string via F1 TextDecomposer → substrate entity_hash.
///      THIS is where cross-model dedup happens: Llama4 token id 1000 +
///      DeepSeek token id 2547 + Qwen token id 9 all attach to the SAME
///      substrate entity if their surfaces are the same string. No mapping
///      table, no per-model translation — content addressing IS the dedup.
///   2. Emit edge: model_entity → has_vocab_token(position=token_id) → token_entity.
///      Per substrate invariant 1: model is content-addressed (model entity
///      hash = composition over the model's safetensors+config artifacts);
///      token is content-addressed (its surface string's substrate entity).
///      No integer model_id, no integer token_id surrogate keys — both
///      identifiers are bytea entity_hashes.
///   3. Emit provenance edge from each token-edge to the model source entity.
///
/// Per the AI-models-as-edge-extraction invariant + the substrate
/// invariants memory: original tensor weights are NOT stored. The model's
/// fireflies (per-token POINT4D positions in the firefly physicality
/// partition) and its semantic edges (extracted via probe-driven
/// activation observation per layer type) come from sibling decomposers
/// (EmbeddingFireflyExtractor, AttentionEdgeExtractor, FfnNeuronEdgeExtractor,
/// LmHeadEdgeExtractor) that consume B19 TensorDecodeService output.
///
/// Phase 4 / Track F5 — first piece of the FIRST PRODUCT slice (G5).
/// </summary>
public sealed class TokenizerAssetDecomposer
{
    private static readonly int[] QuaternaryEdgeRleCounts = new[] { 1, 1, 1, 1 };

    private readonly TextDecomposer          _textDecomposer;
    private readonly IIdentityHashing        _hashing;
    private readonly IConceptEntityResolver  _conceptResolver;
    private readonly IEdgeEmission           _edgeEmission;
    private readonly IProvenance             _provenance;

    public TokenizerAssetDecomposer(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _textDecomposer  = textDecomposer;
        _hashing         = hashing;
        _conceptResolver = conceptResolver;
        _edgeEmission    = edgeEmission;
        _provenance      = provenance;
    }

    /// <summary>
    /// Decompose a tokenizer asset for a model. <paramref name="modelEntityHash"/>
    /// is the content-addressed model entity hash (computed by the caller
    /// from the model's safetensors / config artifacts via Merkle composition).
    /// <paramref name="modelSourceCanonicalName"/> is used to resolve the
    /// per-model provenance source entity (e.g., "huggingface_model_meta_llama_4_maverick").
    ///
    /// Returns a (token_id → substrate token entity hash) dictionary so
    /// downstream firefly extractors can bind tensor row indices to
    /// substrate token entities (embedding row N corresponds to the model's
    /// token with id N, which routes through F1 to a substrate text entity).
    /// </summary>
    public async Task<IReadOnlyDictionary<int, AtomId>> DecomposeAsync(
        AtomId             modelEntityHash,
        string             modelSourceCanonicalName,
        string             tokenizerJsonPath,
        CancellationToken  cancellationToken)
    {
        var sourceHash      = await _provenance.ResolveSourceAsync(modelSourceCanonicalName, cancellationToken).ConfigureAwait(false);
        var hasVocabToken   = _conceptResolver.Resolve("has_vocab_token");
        var roleModel       = _conceptResolver.Resolve("model");
        var roleToken       = _conceptResolver.Resolve("token");
        var rolePosition    = _conceptResolver.Resolve("position");

        var kind  = TokenizerJsonParser.DetectKind(tokenizerJsonPath);
        var vocab = TokenizerJsonParser.Parse(tokenizerJsonPath);

        var tokenIdToSubstrateEntity = new Dictionary<int, AtomId>(vocab.Count);

        foreach (var entry in vocab)
        {
            // 1. Tokenizer surface → canonical text via TokenizerSurfaceDecoder
            //    (strip ## / Ġ / ▁ / GPT-2 byte-fallback per detected kind).
            //    Special tokens pass straight through — they are model-private
            //    structural markers, not lexical content. Without this step,
            //    "##ing" (WordPiece) / "▁ing" (SentencePiece) / "Ġing" (Llama
            //    BPE) would each get a DIFFERENT substrate entity hash —
            //    cross-model dedup would silently break.
            var canonicalSurface = entry.IsSpecial
                ? entry.Surface
                : TokenizerSurfaceDecoder.DecodeToCanonical(kind, entry.Surface);

            // 2. Canonical surface → substrate entity (cross-model dedup automatic).
            var tokenEntityHash = await _textDecomposer.DecomposeAsync(
                canonicalSurface, cancellationToken).ConfigureAwait(false);
            tokenIdToSubstrateEntity[entry.TokenId] = tokenEntityHash;

            // 2. Edge: model → has_vocab_token → token, with position role
            //    carrying the token_id as a substrate-entity-encoded integer
            //    (the integer's digit-codepoint LINESTRING composition hash).
            var positionEntityHash = await _textDecomposer.DecomposeAsync(
                entry.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);

            var edgeHash = _hashing.CompositionId(
                new[] { hasVocabToken, modelEntityHash, tokenEntityHash, positionEntityHash },
                QuaternaryEdgeRleCounts);

            await _edgeEmission.EmitEdgeAsync(
                new EdgeRecord(EdgeTypeHash: hasVocabToken, Hash: edgeHash),
                cancellationToken).ConfigureAwait(false);
            await _edgeEmission.EmitMemberAsync(
                new EdgeMemberRecord(hasVocabToken, edgeHash, roleModel,    0, modelEntityHash),
                cancellationToken).ConfigureAwait(false);
            await _edgeEmission.EmitMemberAsync(
                new EdgeMemberRecord(hasVocabToken, edgeHash, roleToken,    0, tokenEntityHash),
                cancellationToken).ConfigureAwait(false);
            await _edgeEmission.EmitMemberAsync(
                new EdgeMemberRecord(hasVocabToken, edgeHash, rolePosition, 0, positionEntityHash),
                cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEdgeProvenanceAsync(
                new EdgeProvenanceRecord(hasVocabToken, edgeHash, sourceHash),
                cancellationToken).ConfigureAwait(false);
        }

        return tokenIdToSubstrateEntity;
    }
}
