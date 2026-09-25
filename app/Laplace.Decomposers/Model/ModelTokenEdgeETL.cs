using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

public sealed class ModelTokenEdgeETL
{
    internal const int AnalyzerVersion = 10;
    private const string DerivationFamily = "model-circuit-decomposition";
    private const int PlacementWitnesses = 64;

    private static readonly Hash128[] CircuitRelationTypeIds =
    [
        ModelDecomposer.SimilarToTypeId,
        ModelDecomposer.AttendsTypeId,
        ModelDecomposer.OvRelatesTypeId,
        ModelDecomposer.CompletesToTypeId,
    ];

    private static readonly HashSet<Hash128> SymmetricRelations =
    [
        .. new[] { "SIMILAR_TO", "ATTENDS", "OV_RELATES", "COMPLETES_TO" }
            .Where(name => RelationTypeRegistry.Resolve(name).Symmetry == RelationTypeRegistry.Symmetry.Symmetric)
            .Select(RelationTypeRegistry.RelationTypeId),
    ];

    public static int TestimonyWidthPerCircuit => 0;

    public static string ResolvePlanesMode()
    {
        var value = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        string mode = string.IsNullOrWhiteSpace(value)
            ? "structure" : value.Trim().ToLowerInvariant();
        if (mode == "structure") return mode;
        throw new InvalidOperationException(
            $"LAPLACE_MODEL_PLANES='{mode}' is not a valid model-ingest mode; " +
            "model ingestion is one decomposition pass. Raw-factor/model-forward side passes are not authorities.");
    }

    private readonly string _modelDir;
    private readonly ModelManifest _manifest;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly string _sourceName;
    private readonly ILogger _log;
    private readonly int _pageSize =
        IngestSizing.ResolveForSource(IngestSourceProfile.Default).CommitRows;

    internal long PeakNativeResidentBytes { get; private set; }

    public ModelTokenEdgeETL(
        string modelDir, ModelManifest manifest,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, string sourceName, ILogger? log = null)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _source = sourceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        _sourceName = sourceName;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        int commitEpoch,
        ISubstrateReader? reader,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_manifest.TextPlanesRunnable)
        {
            _log.LogInformation(
                "phase=model-circuits: model {Name} has no governed text circuit recipe; header/tokenizer structure remains admitted",
                _manifest.ModelName);
            yield break;
        }

        if (_manifest.Embedding is null)
        {
            _log.LogWarning(
                "phase=model-circuits: checkpoint has no classified embedding tensor");
            yield break;
        }

        ModelConfig cfg = _manifest.Config;
        if (cfg.VocabSize <= 0 || cfg.HiddenSize <= 0) yield break;

        var entities = new List<Hash128>(
            Math.Min(cfg.VocabSize, _tokens.Count));
        var rowByEntity = new Dictionary<Hash128, int>();
        var placements = new Dictionary<Hash128, double[]>();
        foreach (LlamaTokenizerParser.TokenRecord token in _tokens)
        {
            if (token.TokenId < 0 || token.TokenId >= cfg.VocabSize) continue;
            if (!rowByEntity.ContainsKey(token.EntityId))
            {
                rowByEntity.Add(token.EntityId, entities.Count);
                entities.Add(token.EntityId);
            }
            if (token.HasContentCoord && !placements.ContainsKey(token.EntityId))
                placements.Add(token.EntityId,
                    [token.ContentX, token.ContentY, token.ContentZ, token.ContentM]);
        }
        if (entities.Count < 2) yield break;

        SourceEntityIdConventions.ModelContentSnapshot snapshot =
            SourceEntityIdConventions.OpenModelContentSnapshot(_modelDir)
            ?? throw new InvalidDataException(
                "model checkpoint has no weight snapshot");
        using SubstrateApplyEnvelope snapshotOwner =
            SubstrateApplyEnvelope.Own(
                snapshot,
                verifyCt =>
                {
                    verifyCt.ThrowIfCancellationRequested();
                    snapshot.VerifySourceId();
                    return ValueTask.CompletedTask;
                });
        if (snapshot.SourceId != _source)
            throw new InvalidDataException(
                "model checkpoint content changed after source admission; refusing to attribute decomposition to stale source identity");

        var selected = new SelectedModelAnalysisInput(
            _modelDir, _manifest, _tokens, _source, snapshot);
        var circuits = new ModelCircuitEstate(selected, rowByEntity);

        long circuitForms = 0;
        long claimReceipts = 0;
        foreach (Hash128 typeId in CircuitRelationTypeIds)
        {
            foreach (ModelCircuitDescriptor descriptor in circuits.Enumerate(typeId))
            {
                ct.ThrowIfCancellationRequested();

                (long[] salience, int[] order) =
                    descriptor.Contraction.Salience(entities);
                CircuitObservation observation = BuildCircuitObservation(
                    descriptor, entities, placements,
                    salience, order, commitEpoch);
                circuitForms++;
                yield return observation.Change with
                {
                    ApplyEnvelope = snapshotOwner.Retain()
                };

                foreach (SubstrateChange change in EmitSignificantClaims(
                             typeId, descriptor, observation.CircuitId,
                             entities, commitEpoch, ct))
                {
                    claimReceipts += change.Attestations.Length;
                    yield return change with
                    {
                        ApplyEnvelope = snapshotOwner.Retain()
                    };
                }
            }
            PeakNativeResidentBytes = Math.Max(
                PeakNativeResidentBytes, circuits.PeakNativeResidentBytes);
        }
        foreach (string skipped in circuits.Skipped)
            _log.LogWarning("phase=model-circuits: {Skipped}", skipped);

        _log.LogInformation(
            "phase=model-circuits: forms={Forms:N0} significant_claims={Claims:N0} canonical_entities={Entities:N0} skipped={Skipped:N0} raw_weight_bytes_retained=0",
            circuitForms, claimReceipts, entities.Count, circuits.Skipped.Count);
        await Task.CompletedTask;
    }

    private readonly record struct CircuitObservation(
        Hash128 CircuitId, SubstrateChange Change);

    private CircuitObservation BuildCircuitObservation(
        ModelCircuitDescriptor descriptor,
        IReadOnlyList<Hash128> entities,
        IReadOnlyDictionary<Hash128, double[]> placements,
        IReadOnlyList<long> salience,
        IReadOnlyList<int> order,
        int commitEpoch)
    {
        if (salience.Count != entities.Count || order.Count != entities.Count)
            throw new InvalidDataException(
                "native model circuit salience did not preserve canonical entity cardinality");

        var orderedIds = new Hash128[order.Count];
        var orderedScores = new long[order.Count];
        var coordinatePoints = new List<double>(PlacementWitnesses * 4);
        for (int at = 0; at < order.Count; at++)
        {
            int index = order[at];
            if ((uint)index >= (uint)entities.Count)
                throw new InvalidDataException(
                    "native model circuit salience returned an invalid entity ordinal");
            Hash128 entity = entities[index];
            orderedIds[at] = entity;
            orderedScores[at] = salience[index];
            if (coordinatePoints.Count < PlacementWitnesses * 4
                && placements.TryGetValue(entity, out double[]? point))
                coordinatePoints.AddRange(point);
        }
        if (coordinatePoints.Count == 0)
            throw new InvalidDataException(
                $"model circuit {descriptor.Plane}/L{descriptor.Layer}/H{descriptor.Head} has no canonical placed token anchor");

        byte[] packed = TestimonyWalk.Pack(orderedIds, orderedScores);
        var trajectory = new double[packed.Length / sizeof(double)];
        Buffer.BlockCopy(packed, 0, trajectory, 0, packed.Length);
        double[] coordinate = Math4d.Centroid(coordinatePoints.ToArray());
        long observedAt = IngestClock.NowUnixUs();
        double sourceTrust = SourceTrust.AiModelProbe;

        using var builder = new SubstrateChangeBuilder(
                _source,
                $"model/circuit/{descriptor.Plane}/L{descriptor.Layer}/H{descriptor.Head}",
                entityCapacity: 0, physicalityCapacity: 1,
                attestationCapacity: 0)
            .DeclareSourcePrior(sourceTrust)
            .SetCommitEpoch(commitEpoch);

        OrderedCompositionResult circuit = ModelCoordinates.StageCircuit(
            builder, _source, _sourceName,
            descriptor.Plane, descriptor.Layer, descriptor.Head, observedAt);

        builder.AddPhysicality(new PhysicalityRow(
            Id: PhysicalityId.Compute(
                circuit.Id, PhysicalityType.Projection),
            EntityId: circuit.Id,
            SourceId: _source,
            Type: PhysicalityType.Projection,
            CoordX: coordinate[0],
            CoordY: coordinate[1],
            CoordZ: coordinate[2],
            CoordM: coordinate[3],
            HilbertIndex: Hilbert128.Encode(coordinate),
            TrajectoryXyzm: trajectory,
            NConstituents: orderedIds.Length,
            AlignmentResidual: null,
            SourceDim: 1,
            ObservedAtUnixUs: observedAt));

        return new CircuitObservation(circuit.Id, builder.Build());
    }

    /// <summary>
    /// The circuit's pair evidence under its declared significance contract
    /// (native <c>bilinear_contraction_significant_pairs</c>): every ordered pair of
    /// distinct canonical entities is tested against the subject's own score
    /// distribution in this circuit, and only pairs at z >= sqrt(2 ln N) are
    /// written. Every written pair confirms, graded by its departure; a low or
    /// negative score is absence of evidence and is never written as refutation.
    /// Whether another source already holds the pair does not matter: the
    /// model's evidence stands on its own witness.
    /// </summary>
    private IEnumerable<SubstrateChange> EmitSignificantClaims(
        Hash128 typeId,
        ModelCircuitDescriptor descriptor,
        Hash128 circuitId,
        IReadOnlyList<Hash128> vocabulary,
        int commitEpoch,
        CancellationToken ct)
    {
        double sourceTrust = SourceTrust.AiModelProbe;
        int capacity = Math.Max(_pageSize, vocabulary.Count - 1);
        // A symmetric relation's claim is one unordered cell; its family is the
        // unordered pairs, tested once each.
        bool symmetric = SymmetricRelations.Contains(typeId) && descriptor.Contraction.SharedFactors;
        int row = 0;
        while (row < vocabulary.Count)
        {
            ct.ThrowIfCancellationRequested();
            SignificantPairPage page = descriptor.Contraction.SignificantPairs(row, capacity, symmetric);
            if (page.NextRow <= row)
                throw new InvalidDataException("native circuit significance did not advance");
            if (page.Rows.Length > 0)
            {
                using var builder = new SubstrateChangeBuilder(
                        _source,
                        $"model/claim/{descriptor.Plane}/L{descriptor.Layer}/H{descriptor.Head}/{row}",
                        entityCapacity: 0, physicalityCapacity: 0,
                        attestationCapacity: page.Rows.Length)
                    .DeclareSourcePrior(sourceTrust)
                    .SetCommitEpoch(commitEpoch)
                    .SetInputUnitsConsumed(page.Rows.Length);
                for (int i = 0; i < page.Rows.Length; i++)
                {
                    Hash128 subject = vocabulary[page.Rows[i]];
                    Hash128 obj = vocabulary[page.Cols[i]];
                    AttestationRow receipt = NativeAttestation.CategoricalResolvedOutcome(
                        subject, typeId, obj, _source, circuitId, sourceTrust,
                        AttestationOutcome.Confirm);
                    receipt = receipt with
                    {
                        QualifierMask = receipt.QualifierMask | CalculationSources.Qualifier,
                    };
                    builder.AddAttestation(receipt);
                    builder.AddEphemeralFold(new EphemeralFoldInput(
                        receipt.Id,
                        CalculationReceipt(descriptor.ContextId, typeId, subject, obj),
                        page.ScoresFp1e9[i]));
                }
                yield return builder.Build();
            }
            row = page.NextRow;
        }
    }

    internal static Hash128 CircuitContextForVersion(
        int analyzerVersion, Hash128 source,
        string plane, int layer, int head,
        IReadOnlyList<string> tensorNames) =>
        Hash128.OfCanonical(
            $"{DerivationFamily}/v{analyzerVersion}/context/source={source}/plane={plane}/layer={layer}/head={head}/tensors={string.Join('|', tensorNames)}");

    private Hash128 CalculationReceipt(
        Hash128 context, Hash128 typeId,
        Hash128 subject, Hash128 obj) =>
        CalculationReceiptForVersion(
            AnalyzerVersion, _source,
            context, typeId, subject, obj);

    internal static Hash128 CalculationReceiptForVersion(
        int analyzerVersion, Hash128 source, Hash128 context,
        Hash128 typeId, Hash128 subject, Hash128 obj) =>
        Hash128.OfCanonical(
            $"{DerivationFamily}/v{analyzerVersion}/calculation/source={source}/context={context}/type={typeId}/subject={subject}/object={obj}");
}
