using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Phase 3-6 checkpoint contraction. Numeric tensors are temporary operands.
/// The only emitted rows are categorical receipts for already-existing typed
/// token claims, paired with an atomic transient score for the consensus fold.
/// </summary>
public sealed class ModelTokenEdgeETL
{
    internal const int AnalyzerVersion = 6;
    private const string DerivationFamily = "model-existing-claims-bilinear";
    public static int TestimonyWidthPerCircuit => 0;

    public static string ResolvePlanesMode()
    {
        var value = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        string mode = string.IsNullOrWhiteSpace(value) ? "structure" : value.Trim().ToLowerInvariant();
        if (mode == "structure") return mode;
        throw new InvalidOperationException(
            $"LAPLACE_MODEL_PLANES='{mode}' is not a valid model-ingest mode; " +
            "checkpoint contraction is part of the source pass and emits only existing claim-shaped testimony.");
    }

    private readonly string _modelDir;
    private readonly ModelManifest _manifest;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly ILogger _log;
    private readonly int _pageSize = IngestSizing.ResolveForSource(IngestSourceProfile.Default).CommitRows;
    private readonly Dictionary<Hash128, CircuitCandidatePage> _firstPages = new();
    internal long PeakNativeResidentBytes { get; private set; }

    public ModelTokenEdgeETL(string modelDir, ModelManifest manifest,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, Hash128 sourceId,
        ILogger? log = null)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _source = sourceId;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        int commitEpoch,
        ISubstrateReader? reader,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (reader is null || !_manifest.TextPlanesRunnable)
        {
            _log.LogInformation("phase=edges: no readable text claim space for {Name}", _manifest.ModelName);
            yield break;
        }

        TensorRole? embeddingRole = _manifest.Embedding;
        if (embeddingRole is null)
        {
            _log.LogWarning("phase=edges: checkpoint has no classified embedding tensor");
            yield break;
        }

        ModelConfig cfg = _manifest.Config;
        if (cfg.VocabSize <= 0 || cfg.HiddenSize <= 0) yield break;

        var entities = new List<Hash128>(Math.Min(cfg.VocabSize, _tokens.Count));
        var tokenRows = new List<int>(entities.Capacity);
        var tokenEntityIndexes = new List<int>(entities.Capacity);
        var rowByEntity = new Dictionary<Hash128, int>();
        foreach (var token in _tokens)
        {
            if (token.TokenId < 0 || token.TokenId >= cfg.VocabSize) continue;
            if (!rowByEntity.TryGetValue(token.EntityId, out int entityIndex))
            {
                entityIndex = entities.Count;
                rowByEntity.Add(token.EntityId, entityIndex);
                entities.Add(token.EntityId);
            }
            tokenRows.Add(token.TokenId);
            tokenEntityIndexes.Add(entityIndex);
        }
        if (entities.Count == 0) yield break;

        foreach (string relation in new[] { "SIMILAR_TO", "ATTENDS", "OV_RELATES", "COMPLETES_TO" })
        {
            Hash128 typeId = RelationTypeRegistry.RelationTypeId(relation);
            _firstPages[typeId] = await reader.ReadCircuitCandidatesAsync(
                entities, typeId, null, null, _pageSize, ct).ConfigureAwait(false);
        }
        if (_firstPages.Values.All(page => page.Rows.Count == 0))
        {
            _log.LogInformation("phase=edges: model can name no existing typed contraction claims");
            yield break;
        }

        using SourceEntityIdConventions.ModelContentSnapshot snapshot =
            SourceEntityIdConventions.OpenModelContentSnapshot(_modelDir)
            ?? throw new InvalidDataException("model checkpoint has no weight snapshot");
        if (snapshot.SourceId != _source)
            throw new InvalidDataException(
                "model checkpoint content changed after source admission; refusing to attribute contraction to stale source identity");
        IReadOnlyList<SafetensorsContainerParser.TensorReference> refs =
            SafetensorsContainerParser.ParseModel(snapshot);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(refs.Count, StringComparer.Ordinal);
        foreach (var tensor in refs) refMap[tensor.Name] = tensor;

        float[] fullEmbedding = WeightTensorETL.LoadTensorF32(
            refMap, embeddingRole.Name, (long)cfg.VocabSize * cfg.HiddenSize, snapshot);
        if (fullEmbedding.Length == 0)
        {
            _log.LogWarning("phase=edges: embedding dtype {Dtype} is not numerically interpretable", embeddingRole.Dtype);
            yield break;
        }

        int d = cfg.HiddenSize;
        int[] tokenRowMap = tokenRows.ToArray();
        int[] tokenEntityMap = tokenEntityIndexes.ToArray();

        long emitted = 0;
        if (_firstPages[ModelDecomposer.SimilarToTypeId].Rows.Count > 0)
        {
            using var circuit = NativeBilinearContraction.Direct(
                fullEmbedding, fullEmbedding, cfg.VocabSize, d,
                tokenRowMap, tokenEntityMap, entities.Count);
            TrackResident(circuit);
            await foreach (var change in EmitCircuitAsync(
                               "SIMILAR_TO", "embedding", -1, -1,
                               [embeddingRole.Name], circuit,
                               entities, rowByEntity, reader, commitEpoch, ct))
            {
                emitted += change.Attestations.Length;
                yield return change;
            }
        }

        TensorRole? lmHead = _manifest.LmHead;
        if (_firstPages[ModelDecomposer.CompletesToTypeId].Rows.Count > 0
            && lmHead is not null && !string.Equals(lmHead.Name, embeddingRole.Name, StringComparison.Ordinal))
        {
            float[] fullLm = WeightTensorETL.LoadTensorF32(
                refMap, lmHead.Name, (long)cfg.VocabSize * d, snapshot);
            if (fullLm.Length > 0)
            {
                using var circuit = NativeBilinearContraction.Direct(
                    fullEmbedding, fullLm, cfg.VocabSize, d,
                    tokenRowMap, tokenEntityMap, entities.Count);
                TrackResident(circuit);
                await foreach (var change in EmitCircuitAsync(
                                   "COMPLETES_TO", "lm-head", -1, -1,
                                   [lmHead.Name], circuit,
                                   entities, rowByEntity, reader, commitEpoch, ct))
                {
                    emitted += change.Attestations.Length;
                    yield return change;
                }
            }
        }

        for (int layer = 0; layer < _manifest.LayerCount; layer++)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var change in EmitAttentionAsync(
                               layer, fullEmbedding, d, cfg, refMap, snapshot,
                               tokenRowMap, tokenEntityMap, entities, rowByEntity,
                               reader, commitEpoch, ct))
            {
                emitted += change.Attestations.Length;
                yield return change;
            }
            await foreach (var change in EmitValueOutputAsync(
                               layer, fullEmbedding, d, cfg, refMap, snapshot,
                               tokenRowMap, tokenEntityMap, entities, rowByEntity,
                               reader, commitEpoch, ct))
            {
                emitted += change.Attestations.Length;
                yield return change;
            }
            await foreach (var change in EmitFfnAsync(
                               layer, fullEmbedding, d, cfg, refMap, snapshot,
                               tokenRowMap, tokenEntityMap, entities, rowByEntity,
                               reader, commitEpoch, ct))
            {
                emitted += change.Attestations.Length;
                yield return change;
            }
        }

        _log.LogInformation(
            "phase=edges: {Rows:N0} categorical receipts folded from complete existing claim pages; tensor payload retained=0",
            emitted);
    }

    private async IAsyncEnumerable<SubstrateChange> EmitAttentionAsync(
        int layer, float[] embedding, int d, ModelConfig cfg,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refs,
        SourceEntityIdConventions.ModelContentSnapshot snapshot,
        int[] tokenRows, int[] tokenEntityIndexes,
        IReadOnlyList<Hash128> entities, IReadOnlyDictionary<Hash128, int> rowByEntity,
        ISubstrateReader reader, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_firstPages[ModelDecomposer.AttendsTypeId].Rows.Count == 0) yield break;
        TensorRole? qRole = _manifest.Single(layer, TensorRoleKind.AttnQ);
        TensorRole? kRole = _manifest.Single(layer, TensorRoleKind.AttnK);
        if (qRole is null || kRole is null || cfg.NumHeads <= 0 || cfg.HeadDim <= 0) yield break;
        int heads = cfg.NumHeads;
        int kvHeads = Math.Max(1, cfg.NumKvHeads);
        int headDim = cfg.HeadDim;
        int attnDim = checked(heads * headDim);
        int kvDim = checked(kvHeads * headDim);
        float[] q = Load(refs, qRole.Name, (long)attnDim * d, snapshot);
        float[] k = Load(refs, kRole.Name, (long)kvDim * d, snapshot);
        if (q.Length == 0 || k.Length == 0) yield break;
        float[]? qBias = LoadOptionalBias(refs, qRole.Name, attnDim, snapshot);
        float[]? kBias = LoadOptionalBias(refs, kRole.Name, kvDim, snapshot);
        for (int head = 0; head < heads; head++)
        {
            int kvHead = checked(head * kvHeads / heads);
            float[] qHead = SliceRows(q, head * headDim, headDim, d);
            float[] kHead = SliceRows(k, kvHead * headDim, headDim, d);
            float[]? qb = qBias is null ? null : SliceVector(qBias, head * headDim, headDim);
            float[]? kb = kBias is null ? null : SliceVector(kBias, kvHead * headDim, headDim);
            using var circuit = NativeBilinearContraction.Projected(
                embedding, cfg.VocabSize, d, tokenRows, tokenEntityIndexes, entities.Count,
                qHead, qb, kHead, kb, headDim);
            TrackResident(circuit);
            await foreach (var change in EmitCircuitAsync(
                               "ATTENDS", "attention", layer, head,
                               [qRole.Name, kRole.Name], circuit,
                               entities, rowByEntity, reader, commitEpoch, ct))
                yield return change;
        }
    }

    private async IAsyncEnumerable<SubstrateChange> EmitValueOutputAsync(
        int layer, float[] embedding, int d, ModelConfig cfg,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refs,
        SourceEntityIdConventions.ModelContentSnapshot snapshot,
        int[] tokenRows, int[] tokenEntityIndexes,
        IReadOnlyList<Hash128> entities, IReadOnlyDictionary<Hash128, int> rowByEntity,
        ISubstrateReader reader, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_firstPages[ModelDecomposer.OvRelatesTypeId].Rows.Count == 0) yield break;
        TensorRole? vRole = _manifest.Single(layer, TensorRoleKind.AttnV);
        TensorRole? oRole = _manifest.Single(layer, TensorRoleKind.AttnO);
        if (vRole is null || oRole is null || cfg.NumHeads <= 0 || cfg.HeadDim <= 0) yield break;
        int heads = cfg.NumHeads;
        int kvHeads = Math.Max(1, cfg.NumKvHeads);
        int headDim = cfg.HeadDim;
        int attnDim = checked(heads * headDim);
        int kvDim = checked(kvHeads * headDim);
        float[] v = Load(refs, vRole.Name, (long)kvDim * d, snapshot);
        float[] output = Load(refs, oRole.Name, (long)d * attnDim, snapshot);
        if (v.Length == 0 || output.Length == 0) yield break;
        float[]? vBias = LoadOptionalBias(refs, vRole.Name, kvDim, snapshot);
        for (int head = 0; head < heads; head++)
        {
            int kvHead = checked(head * kvHeads / heads);
            float[] vHead = SliceRows(v, kvHead * headDim, headDim, d);
            float[]? vb = vBias is null ? null : SliceVector(vBias, kvHead * headDim, headDim);
            var rightWeight = new float[(long)headDim * d];
            int transposeRc;
            unsafe
            {
                fixed (float* outputPtr = output)
                fixed (float* rightPtr = rightWeight)
                    transposeRc = DynInterop.TransposeColumnBlockF(
                        outputPtr, (nuint)d, (nuint)attnDim,
                        (nuint)(head * headDim), (nuint)headDim, rightPtr);
            }
            if (transposeRc != 0)
                throw new InvalidOperationException($"native output-column contraction failed: {transposeRc}");
            using var circuit = NativeBilinearContraction.Projected(
                embedding, cfg.VocabSize, d, tokenRows, tokenEntityIndexes, entities.Count,
                vHead, vb, rightWeight, null, headDim);
            TrackResident(circuit);
            await foreach (var change in EmitCircuitAsync(
                               "OV_RELATES", "value-output", layer, head,
                               [vRole.Name, oRole.Name], circuit,
                               entities, rowByEntity, reader, commitEpoch, ct))
                yield return change;
        }
    }

    private async IAsyncEnumerable<SubstrateChange> EmitFfnAsync(
        int layer, float[] embedding, int d, ModelConfig cfg,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refs,
        SourceEntityIdConventions.ModelContentSnapshot snapshot,
        int[] tokenRows, int[] tokenEntityIndexes,
        IReadOnlyList<Hash128> entities, IReadOnlyDictionary<Hash128, int> rowByEntity,
        ISubstrateReader reader, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_firstPages[ModelDecomposer.CompletesToTypeId].Rows.Count == 0) yield break;
        TensorRole? upRole = _manifest.Single(layer, TensorRoleKind.MlpUp);
        TensorRole? downRole = _manifest.Single(layer, TensorRoleKind.MlpDown);
        int intermediate = cfg.IntermediateSize;
        if (upRole is null || downRole is null || intermediate <= 0) yield break;
        float[] up = Load(refs, upRole.Name, (long)intermediate * d, snapshot);
        float[] down = Load(refs, downRole.Name, (long)d * intermediate, snapshot);
        if (up.Length == 0 || down.Length == 0) yield break;
        float[]? upBias = LoadOptionalBias(refs, upRole.Name, intermediate, snapshot);
        var downTranspose = new float[(long)intermediate * d];
        int transposeRc;
        unsafe
        {
            fixed (float* downPtr = down)
            fixed (float* transposePtr = downTranspose)
                transposeRc = DynInterop.TransposeColumnBlockF(
                    downPtr, (nuint)d, (nuint)intermediate,
                    0, (nuint)intermediate, transposePtr);
        }
        if (transposeRc != 0)
            throw new InvalidOperationException($"native FFN down-projection contraction failed: {transposeRc}");
        using var circuit = NativeBilinearContraction.Projected(
            embedding, cfg.VocabSize, d, tokenRows, tokenEntityIndexes, entities.Count,
            up, upBias, downTranspose, null, intermediate);
        TrackResident(circuit);
        _log.LogInformation(
            "phase=edges: FFN L{Layer} native resident={Resident:N0} bytes arena={Arena:R}",
            layer, circuit.ResidentBytes, circuit.ArenaRms);
        await foreach (var change in EmitCircuitAsync(
                           "COMPLETES_TO", "ffn", layer, -1,
                           [upRole.Name, downRole.Name], circuit,
                           entities, rowByEntity, reader, commitEpoch, ct))
            yield return change;
    }

    private async IAsyncEnumerable<SubstrateChange> EmitCircuitAsync(
        string relationName, string plane, int layer, int head,
        IReadOnlyList<string> tensorNames,
        NativeBilinearContraction circuit,
        IReadOnlyList<Hash128> vocabulary, IReadOnlyDictionary<Hash128, int> rowByEntity,
        ISubstrateReader reader, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Hash128 typeId = RelationTypeRegistry.RelationTypeId(relationName);
        CircuitCandidatePage page = _firstPages[typeId];
        if (page.Rows.Count == 0) yield break;

        Hash128 context = CircuitContext(plane, layer, head, tensorNames);
        double witnessWeight = RelationTypeRegistry.Resolve(relationName).Rank * SourceTrust.AiModelProbe;
        Hash128? afterSubject = null;
        Hash128? afterObject = null;
        while (page.Rows.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new int[page.Rows.Count];
            var cols = new int[page.Rows.Count];
            for (int i = 0; i < page.Rows.Count; i++)
            {
                CircuitRelation candidate = page.Rows[i];
                if (candidate.TypeId != typeId)
                    throw new InvalidDataException("candidate reader returned a relation outside the requested typed claim space");
                if (!rowByEntity.TryGetValue(candidate.Subject, out rows[i])
                    || !rowByEntity.TryGetValue(candidate.Object, out cols[i]))
                    throw new InvalidDataException("candidate reader returned an endpoint outside the selected vocabulary");
            }
            (long[] scores, short[] outcomes) = circuit.Score(rows, cols);

            var builder = new SubstrateChangeBuilder(
                    _source, $"model/contraction/{plane}/L{layer}/H{head}/{afterSubject}/{afterObject}",
                    entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: page.Rows.Count)
                .SetCommitEpoch(commitEpoch)
                .SetInputUnitsConsumed(page.Rows.Count);
            for (int i = 0; i < page.Rows.Count; i++)
            {
                CircuitRelation candidate = page.Rows[i];
                var outcome = (AttestationOutcome)outcomes[i];
                AttestationRow receipt = NativeAttestation.CategoricalResolvedOutcome(
                    candidate.Subject, typeId, candidate.Object, _source, context,
                    witnessWeight, outcome);
                builder.AddAttestation(receipt);
                builder.AddEphemeralFold(new EphemeralFoldInput(
                    receipt.Id, CalculationReceipt(context, typeId, candidate.Subject, candidate.Object), scores[i]));
            }
            yield return builder.Build();

            if (page.NextSubject is not { } nextSubject || page.NextObject is not { } nextObject)
                yield break;
            if (afterSubject == nextSubject && afterObject == nextObject)
                throw new InvalidDataException("candidate keyset reader did not advance");
            afterSubject = nextSubject;
            afterObject = nextObject;
            page = await reader.ReadCircuitCandidatesAsync(
                vocabulary, typeId, afterSubject, afterObject, _pageSize, ct).ConfigureAwait(false);
        }
    }

    private Hash128 CircuitContext(
        string plane, int layer, int head, IReadOnlyList<string> tensorNames) =>
        CircuitContextForVersion(AnalyzerVersion, _source, plane, layer, head, tensorNames);

    internal static Hash128 CircuitContextForVersion(
        int analyzerVersion, Hash128 source, string plane, int layer, int head,
        IReadOnlyList<string> tensorNames) =>
        Hash128.OfCanonical(
            $"{DerivationFamily}/v{analyzerVersion}/context/source={source}/plane={plane}/layer={layer}/head={head}/tensors={string.Join('|', tensorNames)}");

    private Hash128 CalculationReceipt(Hash128 context, Hash128 typeId, Hash128 subject, Hash128 obj) =>
        CalculationReceiptForVersion(AnalyzerVersion, _source, context, typeId, subject, obj);

    internal static Hash128 CalculationReceiptForVersion(
        int analyzerVersion, Hash128 source, Hash128 context,
        Hash128 typeId, Hash128 subject, Hash128 obj) =>
        Hash128.OfCanonical(
            $"{DerivationFamily}/v{analyzerVersion}/calculation/source={source}/context={context}/type={typeId}/subject={subject}/object={obj}");

    private static float[] Load(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refs,
        string tensorName, long elements,
        SourceEntityIdConventions.ModelContentSnapshot snapshot)
    {
        if (!refs.ContainsKey(tensorName)) return Array.Empty<float>();
        return WeightTensorETL.LoadTensorF32(refs, tensorName, elements, snapshot);
    }

    private static float[]? LoadOptionalBias(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refs,
        string weightName, int elements,
        SourceEntityIdConventions.ModelContentSnapshot snapshot)
    {
        string name = ArchitectureProfile.BiasOf(weightName);
        if (!refs.ContainsKey(name)) return null;
        float[] values = WeightTensorETL.LoadTensorF32(refs, name, elements, snapshot);
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

    private void TrackResident(NativeBilinearContraction circuit) =>
        PeakNativeResidentBytes = Math.Max(PeakNativeResidentBytes, circuit.ResidentBytes);
}
