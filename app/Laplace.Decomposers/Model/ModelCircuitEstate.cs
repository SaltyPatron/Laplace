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
    NativeBilinearContraction Contraction)
{
    public string SourcePath { get; init; } = "";
    public string Operator { get; init; } = "";
}

/// <summary>
/// Transient numeric view of one held model artifact over a caller-selected
/// canonical vocabulary. It opens no paths: headers and tensor bytes are read
/// only through the selected artifact snapshot.
/// </summary>
internal sealed class ModelCircuitEstate
{
    private readonly SelectedModelAnalysisInput _model;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refs;
    private readonly Dictionary<string, TensorRole> _roles;
    private readonly float[] _embedding;
    private float[]? _output;

    private TensorRole OutputRole => _model.Manifest.LmHead ?? _model.Manifest.Embedding!;

    private float[] OutputRows
    {
        get
        {
            if (_output is not null) return _output;
            if (OutputRole.Name == _model.Manifest.Embedding!.Name) return _output = _embedding;
            _output = Load(OutputRole.Name, (long)_cfg.VocabSize * _cfg.HiddenSize);
            if (_output.Length == 0)
                throw new InvalidDataException($"Declared output tensor '{OutputRole.Name}' has no numeric interpretation.");
            return _output;
        }
    }
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
        _roles = model.Manifest.Roles.ToDictionary(role => role.Name, StringComparer.Ordinal);
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

    private readonly List<string> _skipped = [];

    /// <summary>Circuits the recognized anatomy could not feed, each with its reason.
    /// A skipped circuit is reported; it is never replaced by another operator.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

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
            float[] lm = OutputRows;
            if (lm.Length > 0)
            {
                using NativeBilinearContraction circuit = Direct(lm);
                yield return Describe("lm-head", -1, -1, [lmHead.Name], circuit);
            }
        }

        foreach (OperatorInstance op in _model.Manifest.Anatomy.Operators.Where(o => o.Family is "mlp" or "experts"))
        {
            TensorRole? upRole = Operand(op, "up");
            TensorRole? downRole = Operand(op, "down");
            TensorRole? gateRole = Operand(op, "gate");
            int intermediate = upRole is { Shape.Length: 2 }
                ? upRole.Shape[upRole.Orientation == "in,out" ? 1 : 0] : 0;
            if (upRole is null || downRole is null || intermediate <= 0)
            {
                _skipped.Add($"ffn {op.SourcePath} ({op.Operator}): no recognized split up/down projections");
                continue;
            }
            if (_model.Manifest.FfnActivation(gateRole is not null) is not { } activation)
            {
                _skipped.Add($"ffn {op.SourcePath} ({op.Operator}): declared activation '{_cfg.HiddenAct}' has no native operator");
                continue;
            }
            float[] up = LoadProjection(upRole, intermediate, d);
            float[] down = LoadProjection(downRole, d, intermediate);
            float[]? gate = gateRole is not null ? LoadProjection(gateRole, intermediate, d) : null;
            if (up.Length == 0 || down.Length == 0 || gate is { Length: 0 })
            {
                _skipped.Add($"ffn {op.SourcePath} ({op.Operator}): projections have no numeric interpretation");
                continue;
            }
            float[]? upBias = LoadOptionalBias(upRole.Bias, intermediate);
            float[]? gateBias = LoadOptionalBias(gateRole?.Bias, intermediate);
            float[]? downBias = LoadOptionalBias(downRole.Bias, d);
            using NativeBilinearContraction circuit = NativeBilinearContraction.Ffn(
                _embedding, _cfg.VocabSize, d, _tokenRows, _entityRows, _entityCount,
                up, upBias, gate, gateBias, down, downBias, intermediate,
                activation, OutputRows);
            Track(circuit);
            var tensors = new List<string> { embeddingRole.Name, upRole.Name, downRole.Name, OutputRole.Name };
            if (gateRole is not null) tensors.Add(gateRole.Name);
            if (upBias is not null) tensors.Add(upRole.Bias!);
            if (gateBias is not null) tensors.Add(gateRole!.Bias!);
            if (downBias is not null) tensors.Add(downRole.Bias!);
            yield return Describe(
                "ffn", op.Block, -1, tensors, circuit, op);
        }
    }

    private IEnumerable<ModelCircuitDescriptor> AttentionCircuits()
    {
        int d = _cfg.HiddenSize;
        foreach (OperatorInstance op in _model.Manifest.Anatomy.Operators.Where(o => o.Family == "attention"))
        {
            TensorRole? qRole = Operand(op, "q");
            TensorRole? kRole = Operand(op, "k");
            if (qRole is null || kRole is null)
            {
                _skipped.Add($"attention {op.SourcePath} ({op.Operator}): no recognized split query/key projections");
                continue;
            }
            if (!AttentionDimensions(op, out int heads, out int kvHeads, out int headDim)) continue;
            int attnDim = checked(heads * headDim), kvDim = checked(kvHeads * headDim);
            float[] q = LoadProjection(qRole, attnDim, d);
            float[] k = LoadProjection(kRole, kvDim, d);
            if (q.Length == 0 || k.Length == 0)
            {
                _skipped.Add($"attention {op.SourcePath} ({op.Operator}): projections have no numeric interpretation");
                continue;
            }
            float[]? qBias = LoadOptionalBias(qRole.Bias, attnDim);
            float[]? kBias = LoadOptionalBias(kRole.Bias, kvDim);
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
                    "attention", op.Block, head, [qRole.Name, kRole.Name], circuit, op);
            }
        }
    }

    private IEnumerable<ModelCircuitDescriptor> ValueOutputCircuits()
    {
        int d = _cfg.HiddenSize;
        foreach (OperatorInstance op in _model.Manifest.Anatomy.Operators.Where(o => o.Family == "attention"))
        {
            TensorRole? vRole = Operand(op, "v");
            TensorRole? oRole = Operand(op, "o");
            if (vRole is null || oRole is null)
            {
                _skipped.Add($"value-output {op.SourcePath} ({op.Operator}): no recognized split value/output projections");
                continue;
            }
            if (!AttentionDimensions(op, out int heads, out int kvHeads, out int headDim)) continue;
            int attnDim = checked(heads * headDim), kvDim = checked(kvHeads * headDim);
            float[] v = LoadProjection(vRole, kvDim, d);
            float[] output = LoadProjection(oRole, d, attnDim);
            if (v.Length == 0 || output.Length == 0)
            {
                _skipped.Add($"value-output {op.SourcePath} ({op.Operator}): projections have no numeric interpretation");
                continue;
            }
            float[]? vBias = LoadOptionalBias(vRole.Bias, kvDim);
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
                    rightWeight, null, headDim, OutputRows);
                yield return Describe(
                    "value-output", op.Block, head, [_model.Manifest.Embedding!.Name, vRole.Name, oRole.Name, OutputRole.Name], circuit, op);
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
        float[] rightWeight, float[]? rightBias, int rank, float[]? outputRows = null)
    {
        NativeBilinearContraction circuit = NativeBilinearContraction.Projected(
            _embedding, _cfg.VocabSize, _cfg.HiddenSize,
            _tokenRows, _entityRows, _entityCount,
            leftWeight, leftBias, rightWeight, rightBias, rank, outputRows);
        Track(circuit);
        return circuit;
    }

    private ModelCircuitDescriptor Describe(
        string plane, int layer, int head, IReadOnlyList<string> tensorNames,
        NativeBilinearContraction circuit, OperatorInstance? op = null) =>
        new(
            ModelTokenEdgeETL.CircuitContextForVersion(
                ModelTokenEdgeETL.AnalyzerVersion, _model.SourceId,
                plane, layer, head, tensorNames, op?.SourcePath, op?.Operator),
            plane, layer, head, tensorNames, circuit)
        { SourcePath = op?.SourcePath ?? "", Operator = op?.Operator ?? "" };

    private TensorRole? Operand(OperatorInstance op, string slot)
    {
        SlotBinding? binding = op.Slot(slot);
        if (binding is { IsAmbiguous: true })
            throw new InvalidDataException($"{op.SourcePath} ({op.Operator}): ambiguous '{slot}' operand");
        if (binding?.Tensor is not { } name) return null;
        if (!_roles.TryGetValue(name, out TensorRole? role) || role.SourcePath != op.SourcePath
            || role.Operator != op.Operator || role.Slot != slot)
            throw new InvalidDataException($"{op.SourcePath} ({op.Operator}): '{slot}' lost its source binding");
        return role;
    }

    private bool AttentionDimensions(OperatorInstance op, out int heads, out int kvHeads, out int headDim)
    {
        int Dimension(string symbol, int fallback) => op.ContextSymbols.TryGetValue(symbol, out long value)
            ? checked((int)value) : fallback;
        heads = Dimension("h", _cfg.NumHeads);
        kvHeads = Dimension("h_kv", _cfg.NumKvHeads);
        headDim = Dimension("d_h", _cfg.HeadDim);
        if (heads > 0 && kvHeads > 0 && headDim > 0 && heads % kvHeads == 0) return true;
        _skipped.Add($"attention {op.SourcePath} ({op.Operator}): invalid scoped head dimensions h={heads}, h_kv={kvHeads}, d_h={headDim}");
        return false;
    }

    private float[] LoadProjection(TensorRole role, int rows, int cols)
    {
        bool transposed = role.Orientation == "in,out";
        if (role.Orientation is not ("out,in" or "in,out" or "-"))
            throw new InvalidDataException($"projection '{role.Name}' has undecided orientation '{role.Orientation}'");
        if (role.Shape.Length != 2 || role.Shape[0] != (transposed ? cols : rows)
            || role.Shape[1] != (transposed ? rows : cols))
            throw new InvalidDataException($"projection '{role.Name}' source shape disagrees with its scoped {rows}x{cols} operands");
        float[] values = Load(role.Name, (long)rows * cols);
        if (!transposed || values.Length == 0) return values;
        var result = new float[values.Length];
        int rc;
        unsafe
        {
            fixed (float* input = values)
            fixed (float* output = result)
                rc = DynInterop.TransposeColumnBlockF(input, (nuint)cols, (nuint)rows, 0, (nuint)rows, output);
        }
        if (rc != 0) throw new InvalidOperationException($"native projection transpose failed: {rc}");
        return result;
    }

    private float[] Load(string tensorName, long elements) =>
        _refs.ContainsKey(tensorName)
            ? WeightTensorETL.LoadTensorF32(
                _refs, tensorName, elements, _model.Snapshot)
            : Array.Empty<float>();

    private float[]? LoadOptionalBias(string? name, int elements)
    {
        if (name is null || !_refs.ContainsKey(name)) return null;
        float[] values = WeightTensorETL.LoadTensorF32(
            _refs, name, elements, _model.Snapshot);
        if (values.Length != elements)
            throw new InvalidDataException($"Declared bias '{name}' could not be read with its source shape.");
        return values;
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
