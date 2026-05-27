using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Content-addressed entity IDs for transformer latent-axis dimensions.
///
/// Entity ID = Blake3(weight bytes for that dimension) — no model name, no axis
/// label, no dim index in the hash.  Same weight values → same entity.  Two models
/// that share a column (e.g. a fine-tuned model that left that dim unchanged) share
/// the entity and accumulate consensus on it.
///
/// Convention:
///   Residual / embed dims  → column d of embed_tokens.weight   [vocabSize × dModel]
///   KV dims                → row d of layer-0 v_proj.weight    [kvDim × dModel]
///   FFN intermediate dims  → row d of layer-0 gate_proj.weight [interm × dModel]
///
/// Ingest (LlamaWeightExtractor) and synthesis (Program.SynthesizeTinyLlamaAsync)
/// MUST use the same source tensors and this same helper so round-trip IDs match.
/// </summary>
public static class ModelFeatureEntityId
{
    /// <summary>
    /// Hash column <paramref name="col"/> of a BF16 row-major
    /// [<paramref name="numRows"/> × <paramref name="numCols"/>] matrix.
    /// Used for residual/embed dims (column of embed_tokens).
    /// </summary>
    public static Hash128 FromBF16Column(ushort[] matrix, int numRows, int numCols, int col)
    {
        var bytes = new byte[numRows * 2];
        for (int r = 0; r < numRows; r++)
        {
            ushort v = matrix[(long)r * numCols + col];
            bytes[r * 2]     = (byte)(v & 0xFF);
            bytes[r * 2 + 1] = (byte)(v >> 8);
        }
        return Hash128.Blake3(bytes);
    }

    /// <summary>
    /// Hash row <paramref name="row"/> of a BF16 row-major
    /// [numRows × <paramref name="numCols"/>] matrix.
    /// Used for kv dims (row of v_proj) and ffn dims (row of gate_proj).
    /// </summary>
    public static Hash128 FromBF16Row(ushort[] matrix, int numCols, int row)
    {
        var bytes = new byte[numCols * 2];
        int offset = row * numCols;
        for (int c = 0; c < numCols; c++)
        {
            ushort v = matrix[offset + c];
            bytes[c * 2]     = (byte)(v & 0xFF);
            bytes[c * 2 + 1] = (byte)(v >> 8);
        }
        return Hash128.Blake3(bytes);
    }

    /// <summary>
    /// Build entity_id → column_index map from all columns of a BF16 matrix.
    /// For residual dims: pass embed_tokens.weight [vocabSize × dModel], numCols = dModel.
    /// </summary>
    public static Dictionary<Hash128, int> ColumnIndex(ushort[] matrix, int numRows, int numCols)
    {
        var map = new Dictionary<Hash128, int>(numCols);
        for (int col = 0; col < numCols; col++)
            map[FromBF16Column(matrix, numRows, numCols, col)] = col;
        return map;
    }

    /// <summary>
    /// Build entity_id → row_index map from all rows of a BF16 matrix.
    /// For kv dims: pass v_proj [kvDim × dModel], numRows = kvDim.
    /// For ffn dims: pass gate_proj [interm × dModel], numRows = interm.
    /// </summary>
    public static Dictionary<Hash128, int> RowIndex(ushort[] matrix, int numRows, int numCols)
    {
        var map = new Dictionary<Hash128, int>(numRows);
        for (int row = 0; row < numRows; row++)
            map[FromBF16Row(matrix, numCols, row)] = row;
        return map;
    }
}
