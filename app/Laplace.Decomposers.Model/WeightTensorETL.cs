using Microsoft.Extensions.Logging;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Placement axis of the model ETL + the shared exact tensor loader.
///
///  - <see cref="EmitS3MorphAsync"/> — embed_tokens placed on the shared
///    Unicode S³ frame as Projection physicalities (the per-model embed
///    species; Voronoi/Karcher = the geometric consensus that aligns the
///    model's axis entities cross-model).
///  - <see cref="LoadTensorF32"/> — the one exact dtype→f32 cell loader the
///    ETL streams tables through (<see cref="ModelTableETL"/> owns the
///    relation axis: cells → adjudicated matches under the tensor-role kinds).
///
/// Never here, never anywhere: forward passes, probes, GEMM pre-joins,
/// floors/top-k, per-token magnitude reduction, weight storage.
/// </summary>
public sealed class WeightTensorETL
{
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _sourceId;
    private readonly Hash128 _tokenizerEntityId;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly ILogger _log;

    public WeightTensorETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        Hash128 tokenizerEntityId,
        ILogger? log = null)
    {
        _recipe   = recipe;
        _tokens   = tokens;
        _sourceId = sourceId;
        _tokenizerEntityId = tokenizerEntityId;
        _log      = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        // Sharded-aware: union of every *.safetensors shard; each tensor carries its FilePath.
        _refs     = SafetensorsContainerParser.ParseModel(modelDir);
    }

    /// <summary>
    /// Placement axis: morph <c>embed_tokens</c> onto the shared S³ Unicode frame and emit
    /// one Projection physicality per token — the per-model embed species. Streams the
    /// embedding table; never a recompute.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitS3MorphAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;
        int vocabSize = _recipe.VocabSize, dModel = _recipe.HiddenSize;
        float[] embed = LoadTensorF32(refMap, "model.embed_tokens.weight",
            (long)vocabSize * dModel);
        var morph = new TokenS3Morph(embed, vocabSize, dModel, _tokens, _sourceId, _tokenizerEntityId, _log);
        foreach (var change in morph.Emit())
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
            await Task.Yield();
        }
    }

    /* ── The one exact tensor-cell loader ─────────────────────────────────── */

    public static byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var tref = refMap[name];
        byte[] rawBytes = new byte[tref.DataLength];
        // Open the SHARD this tensor lives in (sharded-aware); FilePath is set by ParseModel.
        using var fs = new FileStream(tref.FilePath, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, 1 << 16, useAsync: false);
        fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
        int total = 0;
        while (total < rawBytes.Length)
        {
            int n = fs.Read(rawBytes, total, rawBytes.Length - total);
            if (n == 0) throw new IOException($"safetensors: truncated data for {name}");
            total += n;
        }
        return rawBytes;
    }

    public static float[] LoadTensorF32(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] raw = LoadRawBytes(refMap, name);
        float[] result = new float[expectedElements];
        // Generic dtype → f32 dispatch. The dtype is self-describing (safetensors header),
        // so handling a new one is just a decoder, never detection. Covers every safetensors
        // numeric/bool dtype incl. the float8 quant formats; fails loud (never zeros) on
        // anything else (e.g. GGUF block-quant, which is a different container entirely).
        long n = expectedElements;
        unsafe
        {
            fixed (byte* rp = raw)
            fixed (float* op = result)
            {
                switch (tref.Dtype)
                {
                    case "F32":  { float*  s = (float*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "F64":  { double* s = (double*)rp; for (long i = 0; i < n; i++) op[i] = (float)s[i]; break; }
                    case "F16":  { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) op[i] = (float)BitConverter.UInt16BitsToHalf(s[i]); break; }
                    case "BF16": { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) { uint b = (uint)s[i] << 16; float f; Buffer.MemoryCopy(&b, &f, 4, 4); op[i] = f; } break; }
                    case "F8_E5M2": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE5M2(s[i]); break; }
                    case "F8_E4M3": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE4M3(s[i]); break; }
                    case "I64":  { long*  s = (long*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I32":  { int*   s = (int*)rp;   for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I16":  { short* s = (short*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I8":   { sbyte* s = (sbyte*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "U8":   for (long i = 0; i < n; i++) op[i] = rp[i]; break;
                    case "BOOL": for (long i = 0; i < n; i++) op[i] = rp[i] != 0 ? 1f : 0f; break;
                    default:
                        throw new NotSupportedException(
                            $"tensor '{name}' dtype '{tref.Dtype}' has no decoder. safetensors numeric/bool " +
                            "are covered; GGUF block-quant (Q4_K/Q6_K/…) is a separate container needing its " +
                            "own dequantizer. Refusing to ingest zeros.");
                }
            }
        }
        return result;
    }

    /* float8 E5M2 (1-5-2, bias 15) → f32. Same 5-bit exponent/bias as IEEE half, so widen
     * the mantissa into a half and let the half decoder handle normals/subnormals/inf/nan. */
    private static float DecodeE5M2(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 2) & 0x1F, mant = b & 0x3;
        ushort half = (ushort)((sign << 15) | (exp << 10) | (mant << 8));
        return (float)BitConverter.UInt16BitsToHalf(half);
    }

    /* float8 E4M3FN (1-4-3, bias 7, no inf; S.1111.111 = NaN; max 448) → f32. */
    private static float DecodeE4M3(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 3) & 0xF, mant = b & 0x7;
        float v;
        if (exp == 0)                 v = mant * MathF.ScaleB(1f, -9);          // subnormal: mant/8·2⁻⁶
        else if (exp == 15 && mant == 7) v = float.NaN;
        else                          v = (1f + mant * 0.125f) * MathF.ScaleB(1f, exp - 7);
        return sign != 0 ? -v : v;
    }
}
