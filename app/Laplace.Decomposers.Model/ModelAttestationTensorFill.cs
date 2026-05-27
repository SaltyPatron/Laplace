using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Maps substrate attestation rows onto flat tensor buffers for GGUF/safetensors emission.
/// Must stay aligned with <see cref="LlamaWeightExtractor"/> subject/object orientation.
///
/// Feature entity → dim index maps must be built by the caller from the same source
/// weight matrices used at ingest time (Blake3 of weight column/row bytes per
/// <see cref="ModelFeatureEntityId"/>).  This class does not rebuild them internally.
/// </summary>
public static class ModelAttestationTensorFill
{
    public readonly record struct AttestationEdge(Hash128 SubjectId, Hash128 ObjectId, long RatingFp1e9);

    public enum FillOutcome
    {
        Filled,
        SkippedNoMapping,
        SkippedShapeMismatch,
    }

    public readonly record struct CellWrite(int Row, int Col, double Weight, FillOutcome Outcome);

    /// <summary>
    /// Resolve one attestation edge to matrix coordinates.
    ///
    /// <paramref name="entityToToken"/>: token entity → vocab index (subject or object depending on kind).
    /// <paramref name="featureToDim"/>: feature entity → dim index (the non-token endpoint).
    ///
    /// Orientation per ADR 0056:
    ///   EMBEDS         (token, embed_dim)     → row=token, col=dim
    ///   Q_PROJECTS     (token, token)         → row=query_token, col=key_token
    ///   V_PROJECTS     (token, kv_dim)        → row=token, col=kv_dim
    ///   O_PROJECTS     (attn_dim, token)      → row=attn_dim, col=token
    ///   GATES          (token, ffn_dim)       → row=token, col=ffn_dim
    ///   UP_PROJECTS    (token, ffn_dim)       → row=token, col=ffn_dim
    ///   DOWN_PROJECTS  (ffn_dim, token)       → row=ffn_dim, col=token
    ///   OUTPUT_PROJECTS (embed_dim, token)    → row=dim, col=token  (lm_head layout)
    /// </summary>
    public static CellWrite MapEdge(
        AttestationEdge edge,
        Hash128 kindId,
        string tensorName,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyDictionary<Hash128, int> entityToToken,
        IReadOnlyDictionary<Hash128, int>? featureToDim,
        int rows,
        int cols)
    {
        int row, col;

        if (kindId == ModelDecomposer.QProjectsKind)
        {
            if (rows != recipe.VocabSize || cols != recipe.VocabSize)
                return new CellWrite(0, 0, 0, FillOutcome.SkippedShapeMismatch);
            if (!entityToToken.TryGetValue(edge.SubjectId, out row)
                || !entityToToken.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (kindId == ModelDecomposer.EmbedsKind)
        {
            /* subject=token, object=embed_dim → row=token, col=dim */
            if (featureToDim is null
                || !entityToToken.TryGetValue(edge.SubjectId, out row)
                || !featureToDim.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (kindId == ModelDecomposer.VProjectsKind || kindId == ModelDecomposer.GatesKind || kindId == ModelDecomposer.UpProjectsKind)
        {
            /* subject=token, object=feature_dim → row=token, col=dim */
            if (featureToDim is null
                || !entityToToken.TryGetValue(edge.SubjectId, out row)
                || !featureToDim.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (kindId == ModelDecomposer.OProjectsKind || kindId == ModelDecomposer.DownProjectsKind)
        {
            /* subject=feature_dim, object=token → row=dim, col=token */
            if (featureToDim is null
                || !featureToDim.TryGetValue(edge.SubjectId, out row)
                || !entityToToken.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (kindId == ModelDecomposer.OutputProjectsKind)
        {
            /* lm_head: subject=embed_dim, object=token → row=dim, col=token */
            if (featureToDim is null
                || !featureToDim.TryGetValue(edge.SubjectId, out row)
                || !entityToToken.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else
        {
            return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }

        if (row >= rows || col >= cols)
            return new CellWrite(row, col, 0, FillOutcome.SkippedShapeMismatch);

        return new CellWrite(row, col, RatingToWeight(edge.RatingFp1e9), FillOutcome.Filled);
    }

    /// <summary>
    /// Fill <paramref name="tensorBytes"/> from <paramref name="edges"/>.
    /// <paramref name="featureToDim"/>: caller-built feature entity → dim index, or null for token-only kinds.
    /// </summary>
    public static int FillBuffer(
        IEnumerable<AttestationEdge> edges,
        Hash128 kindId,
        string tensorName,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyDictionary<Hash128, int> entityToToken,
        IReadOnlyDictionary<Hash128, int>? featureToDim,
        byte[] tensorBytes,
        int rows,
        int cols,
        int dtype)
    {
        int filled = 0;
        int shapeMismatch = 0;

        foreach (var edge in edges)
        {
            var mapped = MapEdge(edge, kindId, tensorName, recipe, entityToToken, featureToDim, rows, cols);
            if (mapped.Outcome == FillOutcome.Filled)
            {
                WriteCell(tensorBytes, mapped.Row, mapped.Col, cols, dtype, mapped.Weight);
                filled++;
            }
            else if (mapped.Outcome == FillOutcome.SkippedShapeMismatch)
                shapeMismatch++;
        }

        if (filled == 0 && shapeMismatch > 0)
            return -shapeMismatch;
        return filled;
    }

    public static double RatingToWeight(long ratingFp1e9) => ratingFp1e9 / 1e9;

    public static void WriteCell(byte[] tensorBytes, int row, int col, int cols, int dtype, double weight)
    {
        if (dtype == 0)
        {
            float fv = (float)weight;
            uint bits = BitConverter.SingleToUInt32Bits(fv);
            int off = (row * cols + col) * 4;
            tensorBytes[off + 0] = (byte)(bits & 0xFF);
            tensorBytes[off + 1] = (byte)((bits >> 8) & 0xFF);
            tensorBytes[off + 2] = (byte)((bits >> 16) & 0xFF);
            tensorBytes[off + 3] = (byte)((bits >> 24) & 0xFF);
        }
        else
        {
            ushort bf16 = DoubleToBF16(weight);
            int off = (row * cols + col) * 2;
            tensorBytes[off + 0] = (byte)(bf16 & 0xFF);
            tensorBytes[off + 1] = (byte)(bf16 >> 8);
        }
    }

    public static int CountNonZeroCells(byte[] tensorBytes, int dtype)
    {
        int stride = dtype == 0 ? 4 : 2;
        int n = 0;
        for (int off = 0; off < tensorBytes.Length; off += stride)
        {
            if (tensorBytes[off] != 0 || (stride > 1 && tensorBytes[off + 1] != 0))
                n++;
            if (dtype == 0 && stride == 4
                && (tensorBytes[off + 2] != 0 || tensorBytes[off + 3] != 0))
                n++;
        }
        return n;
    }

    private static ushort DoubleToBF16(double v)
    {
        float f = (float)v;
        uint bits = BitConverter.SingleToUInt32Bits(f);
        return (ushort)(bits >> 16);
    }
}
