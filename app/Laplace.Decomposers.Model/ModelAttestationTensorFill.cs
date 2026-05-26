using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Maps substrate attestation rows onto flat tensor buffers for GGUF/safetensors emission.
/// Must stay aligned with <see cref="LlamaWeightExtractor"/> subject/object orientation.
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

    /// <summary>Resolve one attestation edge to matrix coordinates, or why it cannot be placed.</summary>
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
        if (kindId == ModelDecomposer.QProjectsKind && (rows != recipe.VocabSize || cols != recipe.VocabSize))
            return new CellWrite(0, 0, 0, FillOutcome.SkippedShapeMismatch);

        bool tokenFeature = kindId == ModelDecomposer.EmbedsKind;
        bool featureToken = kindId == ModelDecomposer.OutputProjectsKind;
        bool featureTokenRowsAreTokens = tensorName == "lm_head.weight" && featureToken;

        if (featureToDim is null && !tokenFeature && !featureToken)
            return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);

        if (tensorName == "model.embed_tokens.weight" && !tokenFeature)
            return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);

        int row, col;

        if (kindId == ModelDecomposer.QProjectsKind)
        {
            if (!entityToToken.TryGetValue(edge.SubjectId, out row)
                || !entityToToken.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (tokenFeature)
        {
            if (featureToDim is null
                || !entityToToken.TryGetValue(edge.SubjectId, out row)
                || !featureToDim.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (featureTokenRowsAreTokens)
        {
            if (featureToDim is null
                || !entityToToken.TryGetValue(edge.ObjectId, out row)
                || !featureToDim.TryGetValue(edge.SubjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else if (featureToken)
        {
            if (featureToDim is null
                || !featureToDim.TryGetValue(edge.SubjectId, out row)
                || !entityToToken.TryGetValue(edge.ObjectId, out col))
                return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);
        }
        else
            return new CellWrite(0, 0, 0, FillOutcome.SkippedNoMapping);

        if (row >= rows || col >= cols)
            return new CellWrite(row, col, 0, FillOutcome.SkippedShapeMismatch);

        return new CellWrite(row, col, RatingToWeight(edge.RatingFp1e9), FillOutcome.Filled);
    }

  public static bool TryGetFeatureIndex(
        Hash128 kindId,
        string tensorName,
        LlamaRecipeExtractor.RecipeInfo recipe,
        out string axis,
        out int featureCount,
        out bool featureTokenRowsAreTokens)
    {
        axis = "";
        featureCount = 0;
        featureTokenRowsAreTokens = false;

        if (kindId == ModelDecomposer.EmbedsKind && tensorName == "model.embed_tokens.weight")
        {
            axis = "d";
            featureCount = recipe.HiddenSize;
            return true;
        }

        if (kindId == ModelDecomposer.OutputProjectsKind && tensorName == "lm_head.weight")
        {
            axis = "d";
            featureCount = recipe.HiddenSize;
            featureTokenRowsAreTokens = true;
            return true;
        }

        return false;
    }

    public static int FillBuffer(
        IEnumerable<AttestationEdge> edges,
        Hash128 kindId,
        string tensorName,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyDictionary<Hash128, int> entityToToken,
        byte[] tensorBytes,
        int rows,
        int cols,
        int dtype)
    {
        IReadOnlyDictionary<Hash128, int>? featureToDim = null;
        if (TryGetFeatureIndex(kindId, tensorName, recipe, out string axis, out int featureCount, out _))
            featureToDim = ModelFeatureEntityId.BuildIndex(axis, featureCount);

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
