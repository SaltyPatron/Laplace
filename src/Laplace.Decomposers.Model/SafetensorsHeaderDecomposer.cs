namespace Laplace.Decomposers.Model;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F5 — SafetensorsHeaderDecomposer. Reads a HuggingFace .safetensors file's
/// header via B19 TensorReader and emits substrate entities for each
/// declared tensor, plus a typed has_tensor edge linking the model to the
/// tensor entity.
///
/// Per-tensor entity is content-addressed over [name_text_entity,
/// dtype_concept_entity, shape_text_entity] — same tensor (same name + dtype
/// + shape) across multiple ingestions of the same model produces the SAME
/// entity hash via Merkle composition. Cross-model dedup is automatic when
/// two different models declare a tensor with identical name/dtype/shape.
///
/// Per substrate invariant 8: AI model ingestion = semantic edge extraction.
/// The header decomposer emits the structural skeleton (model has these
/// tensors with these shapes); subsequent per-tensor extractors (Embedding /
/// Attention / FFN / etc.) emit the actual entity-to-entity edges by reading
/// tensor bytes via the same TensorReader.
///
/// Phase 4 / Track F5 / G5.
/// </summary>
public sealed class SafetensorsHeaderDecomposer
{
    private static readonly int[] TernaryEdgeRleCounts    = new[] { 1, 1, 1 };
    private static readonly int[] QuaternaryEdgeRleCounts = new[] { 1, 1, 1, 1 };

    private readonly ITensorReader          _tensorReader;
    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly IProvenance            _provenance;

    public SafetensorsHeaderDecomposer(
        ITensorReader          tensorReader,
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _tensorReader     = tensorReader;
        _textDecomposer   = textDecomposer;
        _hashing          = hashing;
        _conceptResolver  = conceptResolver;
        _edgeEmission     = edgeEmission;
        _provenance       = provenance;
    }

    /// <summary>
    /// Decompose a .safetensors file header.
    /// <paramref name="modelEntityHash"/> is the content-addressed model entity
    /// hash; <paramref name="modelSourceCanonicalName"/> resolves the per-model
    /// provenance source entity (e.g., "huggingface_model_meta_llama_4_maverick").
    /// </summary>
    public async Task<int> DecomposeAsync(
        AtomId             modelEntityHash,
        string             modelSourceCanonicalName,
        string             safetensorsPath,
        CancellationToken  cancellationToken)
    {
        var sourceHash    = await _provenance.ResolveSourceAsync(modelSourceCanonicalName, cancellationToken)
            .ConfigureAwait(false);
        var hasTensor     = _conceptResolver.Resolve("has_tensor");
        var roleModel     = _conceptResolver.Resolve("model");
        var roleTensor    = _conceptResolver.Resolve("tensor");

        using var handle = _tensorReader.Open(safetensorsPath);

        var emitted = 0;
        foreach (var entry in handle.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-tensor entity = composition over [name, dtype, shape].
            // Each constituent is itself a substrate text entity via F1.
            var nameEntity  = await _textDecomposer.DecomposeAsync(entry.Name, cancellationToken)
                .ConfigureAwait(false);
            var dtypeEntity = await _textDecomposer.DecomposeAsync(entry.Dtype.ToString(), cancellationToken)
                .ConfigureAwait(false);
            var shapeText   = ShapeAsText(entry.Shape);
            var shapeEntity = await _textDecomposer.DecomposeAsync(shapeText, cancellationToken)
                .ConfigureAwait(false);

            var tensorEntityHash = _hashing.CompositionId(
                new[] { nameEntity, dtypeEntity, shapeEntity },
                TernaryEdgeRleCounts);

            // Edge: model → has_tensor → tensor (via composition of the 4 hashes).
            var edgeHash = _hashing.CompositionId(
                new[] { hasTensor, modelEntityHash, tensorEntityHash, nameEntity },
                QuaternaryEdgeRleCounts);

            await _edgeEmission.EmitEdgeAsync(
                new EdgeRecord(EdgeTypeHash: hasTensor, Hash: edgeHash),
                cancellationToken).ConfigureAwait(false);
            await _edgeEmission.EmitMemberAsync(
                new EdgeMemberRecord(hasTensor, edgeHash, roleModel,  0, modelEntityHash),
                cancellationToken).ConfigureAwait(false);
            await _edgeEmission.EmitMemberAsync(
                new EdgeMemberRecord(hasTensor, edgeHash, roleTensor, 0, tensorEntityHash),
                cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEdgeProvenanceAsync(
                new EdgeProvenanceRecord(hasTensor, edgeHash, sourceHash),
                cancellationToken).ConfigureAwait(false);

            emitted++;
        }

        return emitted;
    }

    private static string ShapeAsText(long[] shape)
    {
        if (shape.Length == 0) { return "[]"; }
        var parts = new string[shape.Length];
        for (var i = 0; i < shape.Length; i++) {
            parts[i] = shape[i].ToString(CultureInfo.InvariantCulture);
        }
        return "[" + string.Join(",", parts) + "]";
    }
}
