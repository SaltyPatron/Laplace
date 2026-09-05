using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// One selected model artifact whose handles remain owned by the surrounding
/// analysis run. The generic artifact worker enumerates and opens the estate;
/// this value passes that exact snapshot into contraction without reopening it.
/// </summary>
public sealed record SelectedModelAnalysisInput(
    string ModelDirectory,
    ModelManifest Manifest,
    IReadOnlyList<LlamaTokenizerParser.TokenRecord> Tokens,
    Hash128 SourceId,
    SourceEntityIdConventions.ModelContentSnapshot Snapshot);

/// <summary>
/// One atomic multi-source OP9 unit. Each change retains its real model source;
/// OrchestrationId names the selected artifact estate, OP3 basis and page.
/// </summary>
public sealed class ModelCorroborationWorkingSet
{
    private readonly SourceEntityIdConventions.ModelContentSnapshot[] _sourceSnapshots;

    internal ModelCorroborationWorkingSet(
        Hash128 orchestrationId,
        IReadOnlyList<SubstrateChange> changes,
        int proposedPairs,
        int admittedPairs,
        params SourceEntityIdConventions.ModelContentSnapshot[] sourceSnapshots)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(sourceSnapshots);
        if (changes.Count == 0 || sourceSnapshots.Length == 0)
            throw new ArgumentException("a corroboration working set requires changes and held source snapshots");
        OrchestrationId = orchestrationId;
        Changes = changes;
        ProposedPairs = proposedPairs;
        AdmittedPairs = admittedPairs;
        _sourceSnapshots = (SourceEntityIdConventions.ModelContentSnapshot[])sourceSnapshots.Clone();
    }

    public Hash128 OrchestrationId { get; }
    public IReadOnlyList<SubstrateChange> Changes { get; }
    public int ProposedPairs { get; }
    public int AdmittedPairs { get; }

    public Task<ApplyResult> ApplyAsync(
        ISubstrateWriter writer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.ApplyWorkingSetAsync(
            Changes,
            verifyCt =>
            {
                verifyCt.ThrowIfCancellationRequested();
                foreach (SourceEntityIdConventions.ModelContentSnapshot snapshot in _sourceSnapshots)
                    snapshot.VerifySourceId();
                return ValueTask.CompletedTask;
            },
            ct);
    }
}

/// <summary>
/// Phase-5b embedding-similarity admission for two independent selected model
/// artifacts. Existing graph cells only nominate bounded endpoint pairs. Both
/// models must independently produce the same non-draw SIMILAR_TO outcome;
/// their source-scoped circuit receipts and transient scores are then returned
/// together for one journaled multi-source working-set apply.
/// </summary>
public sealed class ModelSimilarityCorroborationETL
{
    private const string Family = "model-pair-similarity-corroboration";
    private readonly SelectedModelAnalysisInput _left;
    private readonly SelectedModelAnalysisInput _right;
    private readonly int _pageSize;

    public ModelSimilarityCorroborationETL(
        SelectedModelAnalysisInput left,
        SelectedModelAnalysisInput right,
        int? pageSize = null)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        if (left.SourceId == right.SourceId)
            throw new ArgumentException("Phase-5b requires two independent model artifact identities");
        if (left.Snapshot.SourceId != left.SourceId || right.Snapshot.SourceId != right.SourceId)
            throw new InvalidDataException("selected model snapshot does not match its admitted source identity");
        _pageSize = pageSize
            ?? IngestSizing.ResolveForSource(IngestSourceProfile.Default).CommitRows;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_pageSize);
    }

    public async IAsyncEnumerable<ModelCorroborationWorkingSet> AnalyzeAsync(
        int commitEpoch,
        ISubstrateReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Hash128 typeId = ModelDecomposer.SimilarToTypeId;
        Hash128[] vocabulary = CommonVocabulary(_left.Tokens, _right.Tokens);
        if (vocabulary.Length == 0) yield break;
        var rowByEntity = new Dictionary<Hash128, int>(vocabulary.Length);
        for (int i = 0; i < vocabulary.Length; i++) rowByEntity.Add(vocabulary[i], i);

        using NativeBilinearContraction leftCircuit = OpenEmbeddingCircuit(_left, rowByEntity);
        using NativeBilinearContraction rightCircuit = OpenEmbeddingCircuit(_right, rowByEntity);
        Hash128 leftContext = ModelTokenEdgeETL.CircuitContextForVersion(
            ModelTokenEdgeETL.AnalyzerVersion, _left.SourceId,
            "embedding", -1, -1, [_left.Manifest.Embedding!.Name]);
        Hash128 rightContext = ModelTokenEdgeETL.CircuitContextForVersion(
            ModelTokenEdgeETL.AnalyzerVersion, _right.SourceId,
            "embedding", -1, -1, [_right.Manifest.Embedding!.Name]);
        double witnessWeight = RelationTypeRegistry.Resolve("SIMILAR_TO").Rank
                               * SourceTrust.AiModelProbe;

        Hash128? afterSubject = null;
        Hash128? afterObject = null;
        CircuitPairProposalPage page = await reader.ReadCircuitPairProposalsAsync(
            vocabulary, typeId, targetSymmetric: true,
            afterSubject, afterObject, _pageSize, ct).ConfigureAwait(false);
        while (page.Rows.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int[] rows = new int[page.Rows.Count];
            int[] cols = new int[page.Rows.Count];
            for (int i = 0; i < page.Rows.Count; i++)
            {
                CircuitPairProposal proposal = page.Rows[i];
                if (!rowByEntity.TryGetValue(proposal.Subject, out rows[i])
                    || !rowByEntity.TryGetValue(proposal.Object, out cols[i]))
                    throw new InvalidDataException("OP3 proposal contains an endpoint outside the common selected vocabulary");
                if (proposal.BasisTypeIds.Count == 0 || proposal.BasisTypeIds.Contains(typeId))
                    throw new InvalidDataException("Phase-5b proposal basis must be non-empty and cannot claim target-kind corroboration");
            }

            (long[] leftScores, short[] leftOutcomes) = leftCircuit.Score(rows, cols);
            (long[] rightScores, short[] rightOutcomes) = rightCircuit.Score(rows, cols);
            var admitted = new List<int>(page.Rows.Count);
            for (int i = 0; i < page.Rows.Count; i++)
                if (leftOutcomes[i] == rightOutcomes[i]
                    && leftOutcomes[i] != (short)AttestationOutcome.Draw)
                    admitted.Add(i);

            if (admitted.Count > 0)
            {
                Hash128 orchestrationId = OrchestrationReceipt(
                    _left.SourceId, _right.SourceId, typeId, page.Rows);
                SubstrateChange leftChange = BuildSourceChange(
                    _left.SourceId, leftContext, typeId, page.Rows, admitted,
                    leftScores, leftOutcomes, witnessWeight, orchestrationId, commitEpoch);
                SubstrateChange rightChange = BuildSourceChange(
                    _right.SourceId, rightContext, typeId, page.Rows, admitted,
                    rightScores, rightOutcomes, witnessWeight, orchestrationId, commitEpoch);
                yield return new ModelCorroborationWorkingSet(
                    orchestrationId, [leftChange, rightChange],
                    page.Rows.Count, admitted.Count,
                    _left.Snapshot, _right.Snapshot);
            }

            if (page.NextSubject is not { } nextSubject
                || page.NextObject is not { } nextObject)
                yield break;
            if (afterSubject == nextSubject && afterObject == nextObject)
                throw new InvalidDataException("pair-proposal keyset reader did not advance");
            afterSubject = nextSubject;
            afterObject = nextObject;
            page = await reader.ReadCircuitPairProposalsAsync(
                vocabulary, typeId, targetSymmetric: true,
                afterSubject, afterObject, _pageSize, ct).ConfigureAwait(false);
        }
    }

    private static NativeBilinearContraction OpenEmbeddingCircuit(
        SelectedModelAnalysisInput model,
        IReadOnlyDictionary<Hash128, int> commonRows)
    {
        TensorRole embeddingRole = model.Manifest.Embedding
            ?? throw new InvalidDataException("selected model has no classified embedding tensor");
        ModelConfig cfg = model.Manifest.Config;
        if (!model.Manifest.TextPlanesRunnable || cfg.VocabSize <= 0 || cfg.HiddenSize <= 0)
            throw new InvalidDataException("selected model cannot run text contraction");
        IReadOnlyList<SafetensorsContainerParser.TensorReference> refs =
            SafetensorsContainerParser.ParseModel(model.Snapshot);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            refs.Count, StringComparer.Ordinal);
        foreach (SafetensorsContainerParser.TensorReference tensor in refs)
            refMap[tensor.Name] = tensor;
        float[] embedding = WeightTensorETL.LoadTensorF32(
            refMap, embeddingRole.Name,
            (long)cfg.VocabSize * cfg.HiddenSize, model.Snapshot);
        if (embedding.Length == 0)
            throw new InvalidDataException("selected embedding tensor has no numeric interpretation");

        var tokenRows = new List<int>();
        var entityRows = new List<int>();
        foreach (LlamaTokenizerParser.TokenRecord token in model.Tokens)
        {
            if (token.TokenId < 0 || token.TokenId >= cfg.VocabSize
                || !commonRows.TryGetValue(token.EntityId, out int entityRow))
                continue;
            tokenRows.Add(token.TokenId);
            entityRows.Add(entityRow);
        }
        return NativeBilinearContraction.Direct(
            embedding, embedding, cfg.VocabSize, cfg.HiddenSize,
            tokenRows.ToArray(), entityRows.ToArray(), commonRows.Count);
    }

    private static SubstrateChange BuildSourceChange(
        Hash128 source, Hash128 circuitContext, Hash128 typeId,
        IReadOnlyList<CircuitPairProposal> proposals, IReadOnlyList<int> admitted,
        IReadOnlyList<long> scores, IReadOnlyList<short> outcomes,
        double witnessWeight, Hash128 orchestrationId, int commitEpoch)
    {
        var builder = new SubstrateChangeBuilder(
                source, $"model/corroboration/{orchestrationId}",
                entityCapacity: 0, physicalityCapacity: 0,
                attestationCapacity: admitted.Count)
            .SetCommitEpoch(commitEpoch)
            .SetInputUnitsConsumed(proposals.Count);
        foreach (int i in admitted)
        {
            CircuitPairProposal proposal = proposals[i];
            AttestationRow receipt = NativeAttestation.CategoricalResolvedOutcome(
                proposal.Subject, typeId, proposal.Object, source, circuitContext,
                witnessWeight, (AttestationOutcome)outcomes[i]);
            builder.AddAttestation(receipt);
            builder.AddEphemeralFold(new EphemeralFoldInput(
                receipt.Id,
                CandidateCalculationReceipt(
                    source, circuitContext, typeId, proposal, orchestrationId),
                scores[i]));
        }
        return builder.Build();
    }

    private static Hash128 CandidateCalculationReceipt(
        Hash128 source, Hash128 circuitContext, Hash128 typeId,
        CircuitPairProposal proposal, Hash128 orchestrationId)
    {
        string basis = string.Join('|', proposal.BasisTypeIds
            .OrderBy(static id => id, Hash128BytewiseComparer.Instance));
        return Hash128.OfCanonical(
            $"{Family}/v{ModelTokenEdgeETL.AnalyzerVersion}/calculation/source={source}" +
            $"/context={circuitContext}/type={typeId}/subject={proposal.Subject}" +
            $"/object={proposal.Object}/basis={basis}/orchestration={orchestrationId}");
    }

    private static Hash128 OrchestrationReceipt(
        Hash128 first, Hash128 second, Hash128 typeId,
        IReadOnlyList<CircuitPairProposal> proposals)
    {
        if (first.CompareToBytewise(second) > 0) (first, second) = (second, first);
        string pairs = string.Join(';', proposals.Select(static proposal =>
            $"{proposal.Subject}>{proposal.Object}:" +
            string.Join(',', proposal.BasisTypeIds
                .OrderBy(static id => id, Hash128BytewiseComparer.Instance))));
        return Hash128.OfCanonical(
            $"{Family}/v{ModelTokenEdgeETL.AnalyzerVersion}/sources={first}|{second}" +
            $"/type={typeId}/pairs={pairs}");
    }

    private static Hash128[] CommonVocabulary(
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> left,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> right)
    {
        var selected = new HashSet<Hash128>(left.Select(static token => token.EntityId));
        selected.IntersectWith(right.Select(static token => token.EntityId));
        Hash128[] rows = selected.ToArray();
        Array.Sort(rows, Hash128BytewiseComparer.Instance);
        return rows;
    }

    private sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        public static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
