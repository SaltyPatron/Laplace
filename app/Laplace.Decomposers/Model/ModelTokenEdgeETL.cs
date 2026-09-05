using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

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
    private static readonly Hash128[] CircuitRelationTypeIds =
    [
        ModelDecomposer.SimilarToTypeId,
        ModelDecomposer.AttendsTypeId,
        ModelDecomposer.OvRelatesTypeId,
        ModelDecomposer.CompletesToTypeId,
    ];
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
        }
        if (entities.Count == 0) yield break;

        _firstPages.Clear();
        foreach (Hash128 typeId in CircuitRelationTypeIds)
        {
            _firstPages[typeId] = await reader.ReadCircuitCandidatesAsync(
                entities, typeId, null, null, _pageSize, ct).ConfigureAwait(false);
        }
        if (_firstPages.Values.All(page => page.Rows.Count == 0))
        {
            _log.LogInformation("phase=edges: model can name no existing typed contraction claims");
            yield break;
        }

        SourceEntityIdConventions.ModelContentSnapshot snapshot =
            SourceEntityIdConventions.OpenModelContentSnapshot(_modelDir)
            ?? throw new InvalidDataException("model checkpoint has no weight snapshot");
        using SubstrateApplyEnvelope snapshotOwner = SubstrateApplyEnvelope.Own(
            snapshot,
            verifyCt =>
            {
                verifyCt.ThrowIfCancellationRequested();
                snapshot.VerifySourceId();
                return ValueTask.CompletedTask;
            });
        if (snapshot.SourceId != _source)
            throw new InvalidDataException(
                "model checkpoint content changed after source admission; refusing to attribute contraction to stale source identity");
        var selected = new SelectedModelAnalysisInput(
            _modelDir, _manifest, _tokens, _source, snapshot);
        var circuits = new ModelCircuitEstate(selected, rowByEntity);
        long emitted = 0;
        foreach (Hash128 typeId in CircuitRelationTypeIds)
        {
            if (_firstPages[typeId].Rows.Count == 0) continue;
            foreach (ModelCircuitDescriptor descriptor in circuits.Enumerate(typeId))
            {
                ct.ThrowIfCancellationRequested();
                await foreach (var change in EmitCircuitAsync(
                                   typeId, descriptor.Plane,
                                   descriptor.Layer, descriptor.Head,
                                   descriptor.TensorNames, descriptor.Contraction,
                                   entities, rowByEntity, reader, commitEpoch, ct))
                {
                    emitted += change.Attestations.Length;
                    yield return change with { ApplyEnvelope = snapshotOwner.Retain() };
                }
            }
            PeakNativeResidentBytes = Math.Max(
                PeakNativeResidentBytes, circuits.PeakNativeResidentBytes);
        }

        _log.LogInformation(
            "phase=edges: {Rows:N0} categorical receipts folded from complete existing claim pages; tensor payload retained=0",
            emitted);
    }

    private async IAsyncEnumerable<SubstrateChange> EmitCircuitAsync(
        Hash128 typeId, string plane, int layer, int head,
        IReadOnlyList<string> tensorNames,
        NativeBilinearContraction circuit,
        IReadOnlyList<Hash128> vocabulary, IReadOnlyDictionary<Hash128, int> rowByEntity,
        ISubstrateReader reader, int commitEpoch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        CircuitCandidatePage page = _firstPages[typeId];
        if (page.Rows.Count == 0) yield break;

        Hash128 context = CircuitContext(plane, layer, head, tensorNames);
        // The resolved native builder applies the relation's registered rank.
        // Its scalar argument is source trust, exactly once.
        double sourceTrust = SourceTrust.AiModelProbe;
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
                    sourceTrust, outcome);
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

}
