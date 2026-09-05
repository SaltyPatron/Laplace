using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// One declared circuit and its source-scoped provenance. The native context
/// remains alive only until the circuit enumerator advances.
/// </summary>
internal readonly record struct ModelCircuitDescriptor(
    Hash128 ContextId,
    string Plane,
    int Layer,
    int Head,
    IReadOnlyList<string> TensorNames,
    NativeBilinearContraction Contraction);

/// <summary>
/// Transient numeric view of one held model artifact over a caller-selected
/// canonical vocabulary. It opens no paths: headers and tensor bytes are read
/// only through the selected artifact snapshot.
/// </summary>
internal sealed class ModelCircuitEstate
{
    private readonly SelectedModelAnalysisInput _model;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refs;
    private readonly float[] _embedding;
    private readonly int[] _tokenRows;
    private readonly int[] _entityRows;
    private readonly int _entityCount;
    private readonly ModelConfig _cfg;

    public ModelCircuitEstate(
        SelectedModelAnalysisInput model,
        IReadOnlyDictionary<Hash128, int> canonicalRows)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(canonicalRows);
        if (model.Snapshot.SourceId != model.SourceId)
            throw new InvalidDataException("selected model snapshot does not match its admitted source identity");
        if (!model.Manifest.TextPlanesRunnable || canonicalRows.Count == 0)
            throw new InvalidDataException("selected model cannot run text contraction");
        TensorRole embeddingRole = model.Manifest.Embedding
            ?? throw new InvalidDataException("selected model has no classified embedding tensor");
        _cfg = model.Manifest.Config;
        if (_cfg.VocabSize <= 0 || _cfg.HiddenSize <= 0)
            throw new InvalidDataException("selected model has no runnable text dimensions");

        IReadOnlyList<SafetensorsContainerParser.TensorReference> refs =
            SafetensorsContainerParser.ParseModel(model.Snapshot);
        _refs = new(refs.Count, StringComparer.Ordinal);
        foreach (SafetensorsContainerParser.TensorReference tensor in refs)
            _refs[tensor.Name] = tensor;
        _embedding = WeightTensorETL.LoadTensorF32(
            _refs, embeddingRole.Name,
            (long)_cfg.VocabSize * _cfg.HiddenSize, model.Snapshot);
        if (_embedding.Length == 0)
            throw new InvalidDataException("selected embedding tensor has no numeric interpretation");

        var tokenRows = new List<int>();
        var entityRows = new List<int>();
        foreach (LlamaTokenizerParser.TokenRecord token in model.Tokens)
        {
            if (token.TokenId < 0 || token.TokenId >= _cfg.VocabSize
                || !canonicalRows.TryGetValue(token.EntityId, out int entityRow))
                continue;
            tokenRows.Add(token.TokenId);
            entityRows.Add(entityRow);
        }
        _tokenRows = tokenRows.ToArray();
        _entityRows = entityRows.ToArray();
        _entityCount = canonicalRows.Count;
        if (_tokenRows.Length == 0)
            throw new InvalidDataException("selected model names no canonical analysis entities");
    }

    public long PeakNativeResidentBytes { get; private set; }

    public IEnumerable<ModelCircuitDescriptor> Enumerate(Hash128 targetTypeId)
    {
        if (targetTypeId == ModelDecomposer.SimilarToTypeId)
            return EmbeddingCircuit();
        if (targetTypeId == ModelDecomposer.AttendsTypeId)
            return AttentionCircuits();
        if (targetTypeId == ModelDecomposer.OvRelatesTypeId)
            return ValueOutputCircuits();
        if (targetTypeId == ModelDecomposer.CompletesToTypeId)
            return CompletionCircuits();
        return Array.Empty<ModelCircuitDescriptor>();
    }

    private IEnumerable<ModelCircuitDescriptor> EmbeddingCircuit()
    {
        TensorRole role = _model.Manifest.Embedding!;
        using NativeBilinearContraction circuit = Direct(_embedding);
        yield return Describe("embedding", -1, -1, [role.Name], circuit);
    }

    private IEnumerable<ModelCircuitDescriptor> CompletionCircuits()
    {
        TensorRole embeddingRole = _model.Manifest.Embedding!;
        TensorRole? lmHead = _model.Manifest.LmHead;
        int d = _cfg.HiddenSize;
        if (lmHead is not null
            && !string.Equals(lmHead.Name, embeddingRole.Name, StringComparison.Ordinal))
        {
            float[] lm = Load(lmHead.Name, (long)_cfg.VocabSize * d);
            if (lm.Length > 0)
            {
                using NativeBilinearContraction circuit = Direct(lm);
                yield return Describe("lm-head", -1, -1, [lmHead.Name], circuit);
            }
        }

        for (int layer = 0; layer < _model.Manifest.LayerCount; layer++)
        {
            TensorRole? upRole = _model.Manifest.Single(layer, TensorRoleKind.MlpUp);
            TensorRole? downRole = _model.Manifest.Single(layer, TensorRoleKind.MlpDown);
            int intermediate = _cfg.IntermediateSize;
            if (upRole is null || downRole is null || intermediate <= 0) continue;
            float[] up = Load(upRole.Name, (long)intermediate * d);
            float[] down = Load(downRole.Name, (long)d * intermediate);
            if (up.Length == 0 || down.Length == 0) continue;
            float[]? upBias = LoadOptionalBias(upRole.Name, intermediate);
            var downTranspose = new float[(long)intermediate * d];
            int rc;
            unsafe
            {
                fixed (float* downPtr = down)
                fixed (float* transposePtr = downTranspose)
                    rc = DynInterop.TransposeColumnBlockF(
                        downPtr, (nuint)d, (nuint)intermediate,
                        0, (nuint)intermediate, transposePtr);
            }
            if (rc != 0)
                throw new InvalidOperationException($"native FFN down-projection contraction failed: {rc}");
            using NativeBilinearContraction circuit = Projected(
                up, upBias, downTranspose, null, intermediate);
            yield return Describe(
                "ffn", layer, -1, [upRole.Name, downRole.Name], circuit);
        }
    }

    private IEnumerable<ModelCircuitDescriptor> AttentionCircuits()
    {
        int d = _cfg.HiddenSize;
        if (_cfg.NumHeads <= 0 || _cfg.HeadDim <= 0) yield break;
        int heads = _cfg.NumHeads;
        int kvHeads = Math.Max(1, _cfg.NumKvHeads);
        int headDim = _cfg.HeadDim;
        int attnDim = checked(heads * headDim);
        int kvDim = checked(kvHeads * headDim);
        for (int layer = 0; layer < _model.Manifest.LayerCount; layer++)
        {
            TensorRole? qRole = _model.Manifest.Single(layer, TensorRoleKind.AttnQ);
            TensorRole? kRole = _model.Manifest.Single(layer, TensorRoleKind.AttnK);
            if (qRole is null || kRole is null) continue;
            float[] q = Load(qRole.Name, (long)attnDim * d);
            float[] k = Load(kRole.Name, (long)kvDim * d);
            if (q.Length == 0 || k.Length == 0) continue;
            float[]? qBias = LoadOptionalBias(qRole.Name, attnDim);
            float[]? kBias = LoadOptionalBias(kRole.Name, kvDim);
            for (int head = 0; head < heads; head++)
            {
                int kvHead = checked(head * kvHeads / heads);
                using NativeBilinearContraction circuit = Projected(
                    SliceRows(q, head * headDim, headDim, d),
                    qBias is null ? null : SliceVector(qBias, head * headDim, headDim),
                    SliceRows(k, kvHead * headDim, headDim, d),
                    kBias is null ? null : SliceVector(kBias, kvHead * headDim, headDim),
                    headDim);
                yield return Describe(
                    "attention", layer, head, [qRole.Name, kRole.Name], circuit);
            }
        }
    }

    private IEnumerable<ModelCircuitDescriptor> ValueOutputCircuits()
    {
        int d = _cfg.HiddenSize;
        if (_cfg.NumHeads <= 0 || _cfg.HeadDim <= 0) yield break;
        int heads = _cfg.NumHeads;
        int kvHeads = Math.Max(1, _cfg.NumKvHeads);
        int headDim = _cfg.HeadDim;
        int attnDim = checked(heads * headDim);
        int kvDim = checked(kvHeads * headDim);
        for (int layer = 0; layer < _model.Manifest.LayerCount; layer++)
        {
            TensorRole? vRole = _model.Manifest.Single(layer, TensorRoleKind.AttnV);
            TensorRole? oRole = _model.Manifest.Single(layer, TensorRoleKind.AttnO);
            if (vRole is null || oRole is null) continue;
            float[] v = Load(vRole.Name, (long)kvDim * d);
            float[] output = Load(oRole.Name, (long)d * attnDim);
            if (v.Length == 0 || output.Length == 0) continue;
            float[]? vBias = LoadOptionalBias(vRole.Name, kvDim);
            for (int head = 0; head < heads; head++)
            {
                int kvHead = checked(head * kvHeads / heads);
                var rightWeight = new float[(long)headDim * d];
                int rc;
                unsafe
                {
                    fixed (float* outputPtr = output)
                    fixed (float* rightPtr = rightWeight)
                        rc = DynInterop.TransposeColumnBlockF(
                            outputPtr, (nuint)d, (nuint)attnDim,
                            (nuint)(head * headDim), (nuint)headDim, rightPtr);
                }
                if (rc != 0)
                    throw new InvalidOperationException($"native output-column contraction failed: {rc}");
                using NativeBilinearContraction circuit = Projected(
                    SliceRows(v, kvHead * headDim, headDim, d),
                    vBias is null ? null : SliceVector(vBias, kvHead * headDim, headDim),
                    rightWeight, null, headDim);
                yield return Describe(
                    "value-output", layer, head, [vRole.Name, oRole.Name], circuit);
            }
        }
    }

    private NativeBilinearContraction Direct(float[] right)
    {
        NativeBilinearContraction circuit = NativeBilinearContraction.Direct(
            _embedding, right, _cfg.VocabSize, _cfg.HiddenSize,
            _tokenRows, _entityRows, _entityCount);
        Track(circuit);
        return circuit;
    }

    private NativeBilinearContraction Projected(
        float[] leftWeight, float[]? leftBias,
        float[] rightWeight, float[]? rightBias, int rank)
    {
        NativeBilinearContraction circuit = NativeBilinearContraction.Projected(
            _embedding, _cfg.VocabSize, _cfg.HiddenSize,
            _tokenRows, _entityRows, _entityCount,
            leftWeight, leftBias, rightWeight, rightBias, rank);
        Track(circuit);
        return circuit;
    }

    private ModelCircuitDescriptor Describe(
        string plane, int layer, int head, IReadOnlyList<string> tensorNames,
        NativeBilinearContraction circuit) =>
        new(
            ModelTokenEdgeETL.CircuitContextForVersion(
                ModelTokenEdgeETL.AnalyzerVersion, _model.SourceId,
                plane, layer, head, tensorNames),
            plane, layer, head, tensorNames, circuit);

    private float[] Load(string tensorName, long elements) =>
        _refs.ContainsKey(tensorName)
            ? WeightTensorETL.LoadTensorF32(
                _refs, tensorName, elements, _model.Snapshot)
            : Array.Empty<float>();

    private float[]? LoadOptionalBias(string weightName, int elements)
    {
        string name = ArchitectureProfile.BiasOf(weightName);
        if (!_refs.ContainsKey(name)) return null;
        float[] values = WeightTensorETL.LoadTensorF32(
            _refs, name, elements, _model.Snapshot);
        return values.Length == 0 ? null : values;
    }

    private static float[] SliceRows(float[] matrix, int rowBegin, int rowCount, int rowWidth)
    {
        if (rowBegin < 0 || rowCount <= 0 || rowWidth <= 0
            || (long)(rowBegin + rowCount) * rowWidth > matrix.LongLength)
            throw new ArgumentOutOfRangeException(nameof(rowBegin));
        var result = new float[(long)rowCount * rowWidth];
        Array.Copy(matrix, (long)rowBegin * rowWidth, result, 0, result.LongLength);
        return result;
    }

    private static float[] SliceVector(float[] vector, int begin, int count)
    {
        if (begin < 0 || count <= 0 || begin + count > vector.Length)
            throw new ArgumentOutOfRangeException(nameof(begin));
        var result = new float[count];
        Array.Copy(vector, begin, result, 0, count);
        return result;
    }

    private void Track(NativeBilinearContraction circuit) =>
        PeakNativeResidentBytes = Math.Max(PeakNativeResidentBytes, circuit.ResidentBytes);
}
