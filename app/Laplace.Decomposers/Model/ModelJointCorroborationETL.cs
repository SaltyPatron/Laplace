using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Two-model Phase-5b admission across every currently supported model target
/// kind. Existing non-target graph cells nominate endpoint pairs only. Each
/// model's declared circuits are folded through native Glicko as one source
/// vote; admission requires equal non-draw votes from the independent sources.
/// </summary>
public sealed class ModelJointCorroborationETL
{
    private const string Family = "model-joint-circuit-corroboration";
    private static readonly Hash128[] TargetTypeIds =
    [
        ModelDecomposer.SimilarToTypeId,
        ModelDecomposer.AttendsTypeId,
        ModelDecomposer.OvRelatesTypeId,
        ModelDecomposer.CompletesToTypeId,
    ];

    private readonly SelectedModelAnalysisInput _left;
    private readonly SelectedModelAnalysisInput _right;
    private readonly int _pageSize;

    public ModelJointCorroborationETL(
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

    internal long PeakTransientScoreBytes { get; private set; }
    internal long PeakNativeResidentBytes { get; private set; }

    public async IAsyncEnumerable<ModelCorroborationWorkingSet> AnalyzeAsync(
        int commitEpoch,
        ISubstrateReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Hash128[] vocabulary = CommonVocabulary(_left.Tokens, _right.Tokens);
        if (vocabulary.Length == 0) yield break;
        Dictionary<Hash128, int> rowByEntity = IndexVocabulary(vocabulary);
        var leftEstate = new ModelCircuitEstate(_left, rowByEntity);
        var rightEstate = new ModelCircuitEstate(_right, rowByEntity);
        foreach (Hash128 targetTypeId in TargetTypeIds)
        {
            RelationTypeRegistry.RelationTypeResolution target = ResolveTarget(targetTypeId);
            await foreach (ModelCorroborationWorkingSet set in AnalyzeTargetCoreAsync(
                               target, commitEpoch, reader,
                               vocabulary, leftEstate, rightEstate, ct).ConfigureAwait(false))
                yield return set;
        }
    }

    public async IAsyncEnumerable<ModelCorroborationWorkingSet> AnalyzeTargetAsync(
        string targetName,
        int commitEpoch,
        ISubstrateReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        RelationTypeRegistry.RelationTypeResolution target = ResolveTarget(targetName);
        Hash128[] vocabulary = CommonVocabulary(_left.Tokens, _right.Tokens);
        if (vocabulary.Length == 0) yield break;
        Dictionary<Hash128, int> rowByEntity = IndexVocabulary(vocabulary);
        var leftEstate = new ModelCircuitEstate(_left, rowByEntity);
        var rightEstate = new ModelCircuitEstate(_right, rowByEntity);
        await foreach (ModelCorroborationWorkingSet set in AnalyzeTargetCoreAsync(
                           target, commitEpoch, reader,
                           vocabulary, leftEstate, rightEstate, ct).ConfigureAwait(false))
            yield return set;
    }

    private async IAsyncEnumerable<ModelCorroborationWorkingSet> AnalyzeTargetCoreAsync(
        RelationTypeRegistry.RelationTypeResolution target,
        int commitEpoch,
        ISubstrateReader reader,
        Hash128[] vocabulary,
        ModelCircuitEstate leftEstate,
        ModelCircuitEstate rightEstate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Dictionary<Hash128, int> rowByEntity = IndexVocabulary(vocabulary);

        List<CircuitPairProposal> proposals = await ReadCompleteProposalEstateAsync(
            reader, vocabulary, target.Id,
            target.Symmetry == RelationTypeRegistry.Symmetry.Symmetric, ct).ConfigureAwait(false);
        if (proposals.Count == 0) yield break;
        var rows = new int[proposals.Count];
        var cols = new int[proposals.Count];
        for (int i = 0; i < proposals.Count; i++)
        {
            CircuitPairProposal proposal = proposals[i];
            if (!rowByEntity.TryGetValue(proposal.Subject, out rows[i])
                || !rowByEntity.TryGetValue(proposal.Object, out cols[i]))
                throw new InvalidDataException("OP3 proposal contains an endpoint outside the common selected vocabulary");
            if (proposal.BasisTypeIds.Count == 0 || proposal.BasisTypeIds.Contains(target.Id))
                throw new InvalidDataException("Phase-5b proposal basis must be non-empty and cannot claim target-kind corroboration");
        }

        double sourceTrust = SourceTrust.AiModelProbe;
        ModelTargetVote? leftVote = ScoreModel(
            leftEstate, _left.SourceId, target.Id, sourceTrust, rows, cols);
        Track(leftEstate);
        if (leftVote is null) yield break;
        ModelTargetVote? rightVote = ScoreModel(
            rightEstate, _right.SourceId, target.Id, sourceTrust, rows, cols);
        Track(rightEstate);
        if (rightVote is null) yield break;

        for (int begin = 0; begin < proposals.Count; begin += _pageSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(_pageSize, proposals.Count - begin);
            var page = proposals.GetRange(begin, count);
            var admitted = new List<int>(count);
            for (int local = 0; local < count; local++)
            {
                int i = begin + local;
                if (leftVote.Outcomes[i] == rightVote.Outcomes[i]
                    && leftVote.Outcomes[i] != (short)AttestationOutcome.Draw)
                    admitted.Add(local);
            }
            if (admitted.Count == 0) continue;

            Hash128 orchestration = OrchestrationReceipt(
                _left.SourceId, _right.SourceId, target.Id, page);
            SubstrateChange leftChange = BuildSourceChange(
                _left.SourceId, leftVote.ContextId, target.Id,
                page, admitted, leftVote.Scores.AsSpan(begin, count),
                leftVote.Outcomes.AsSpan(begin, count), sourceTrust,
                orchestration, commitEpoch);
            SubstrateChange rightChange = BuildSourceChange(
                _right.SourceId, rightVote.ContextId, target.Id,
                page, admitted, rightVote.Scores.AsSpan(begin, count),
                rightVote.Outcomes.AsSpan(begin, count), sourceTrust,
                orchestration, commitEpoch);
            yield return new ModelCorroborationWorkingSet(
                orchestration, [leftChange, rightChange], count, admitted.Count,
                _left.Snapshot, _right.Snapshot);
        }
    }

    private static RelationTypeRegistry.RelationTypeResolution ResolveTarget(Hash128 targetTypeId)
    {
        foreach (RelationTypeRegistry.RelationTypeResolution target in RelationTypeRegistry.AllCanonical())
        {
            if (target.Id == targetTypeId && IsDeclaredTarget(target.Id))
                return target;
        }
        throw new InvalidOperationException(
            $"declared model contraction relation {targetTypeId} is not registered");
    }

    private static RelationTypeRegistry.RelationTypeResolution ResolveTarget(string targetName)
    {
        foreach (RelationTypeRegistry.RelationTypeResolution target in RelationTypeRegistry.AllCanonical())
        {
            if (IsDeclaredTarget(target.Id)
                && string.Equals(target.Canonical, targetName, StringComparison.Ordinal))
                return target;
        }
        throw new ArgumentOutOfRangeException(nameof(targetName), targetName,
            "target is not a declared model contraction relation");
    }

    private static bool IsDeclaredTarget(Hash128 typeId)
    {
        foreach (Hash128 declared in TargetTypeIds)
            if (declared == typeId) return true;
        return false;
    }

    private ModelTargetVote? ScoreModel(
        ModelCircuitEstate estate,
        Hash128 source,
        Hash128 targetType,
        double sourceTrust,
        int[] rows,
        int[] cols)
    {
        var circuitScores = new List<long[]>();
        var contexts = new List<Hash128>();
        var opponentRatings = new List<long>();
        var opponentRds = new List<long>();
        short[]? firstCircuitOutcomes = null;
        foreach (ModelCircuitDescriptor circuit in estate.Enumerate(targetType))
        {
            (long[] scores, short[] outcomes) = circuit.Contraction.Score(rows, cols);
            circuitScores.Add(scores);
            firstCircuitOutcomes ??= outcomes;
            contexts.Add(circuit.ContextId);
            AttestationRow prototype = NativeAttestation.CategoricalResolvedOutcome(
                default, targetType, default(Hash128), source, circuit.ContextId,
                sourceTrust, AttestationOutcome.Draw);
            opponentRatings.Add(prototype.OpponentRatingFp1e9);
            opponentRds.Add(prototype.OpponentRdFp1e9);
            PeakTransientScoreBytes = Math.Max(
                PeakTransientScoreBytes,
                checked((long)circuitScores.Count * rows.Length * sizeof(long)));
        }
        if (circuitScores.Count == 0) return null;

        long[] scoresOut;
        short[] outcomesOut;
        if (circuitScores.Count == 1)
        {
            scoresOut = circuitScores[0];
            outcomesOut = firstCircuitOutcomes!;
        }
        else
        {
            (scoresOut, outcomesOut) = NativeBilinearContraction.AggregateCircuitScores(
                circuitScores, opponentRatings, opponentRds);
        }
        Hash128 context = contexts.Count == 1
            ? contexts[0]
            : AggregateContext(source, targetType, contexts);
        return new(context, scoresOut, outcomesOut, contexts.Count);
    }

    private async Task<List<CircuitPairProposal>> ReadCompleteProposalEstateAsync(
        ISubstrateReader reader,
        IReadOnlyList<Hash128> vocabulary,
        Hash128 targetType,
        bool symmetric,
        CancellationToken ct)
    {
        var result = new List<CircuitPairProposal>();
        Hash128? afterSubject = null;
        Hash128? afterObject = null;
        while (true)
        {
            CircuitPairProposalPage page = await reader.ReadCircuitPairProposalsAsync(
                vocabulary, targetType, symmetric,
                afterSubject, afterObject, _pageSize, ct).ConfigureAwait(false);
            if (page.Rows.Count == 0) return result;
            result.AddRange(page.Rows);
            if (page.NextSubject is not { } nextSubject
                || page.NextObject is not { } nextObject)
                return result;
            if (afterSubject == nextSubject && afterObject == nextObject)
                throw new InvalidDataException("pair-proposal keyset reader did not advance");
            afterSubject = nextSubject;
            afterObject = nextObject;
        }
    }

    private static SubstrateChange BuildSourceChange(
        Hash128 source,
        Hash128 context,
        Hash128 targetType,
        IReadOnlyList<CircuitPairProposal> proposals,
        IReadOnlyList<int> admitted,
        ReadOnlySpan<long> scores,
        ReadOnlySpan<short> outcomes,
        double sourceTrust,
        Hash128 orchestration,
        int commitEpoch)
    {
        var builder = new SubstrateChangeBuilder(
                source, $"model/joint-corroboration/{orchestration}",
                entityCapacity: 0, physicalityCapacity: 0,
                attestationCapacity: admitted.Count)
            .SetCommitEpoch(commitEpoch)
            .SetInputUnitsConsumed(proposals.Count);
        foreach (int i in admitted)
        {
            CircuitPairProposal proposal = proposals[i];
            AttestationRow receipt = NativeAttestation.CategoricalResolvedOutcome(
                proposal.Subject, targetType, proposal.Object,
                source, context, sourceTrust,
                (AttestationOutcome)outcomes[i]);
            builder.AddAttestation(receipt);
            builder.AddEphemeralFold(new EphemeralFoldInput(
                receipt.Id,
                CandidateCalculationReceipt(
                    source, context, targetType, proposal, orchestration),
                scores[i]));
        }
        return builder.Build();
    }

    private static Hash128 AggregateContext(
        Hash128 source, Hash128 targetType, IReadOnlyList<Hash128> contexts)
    {
        string members = string.Join('|', contexts);
        return Hash128.OfCanonical(
            $"{Family}/v{ModelTokenEdgeETL.AnalyzerVersion}/aggregate-context" +
            $"/source={source}/type={targetType}/circuits={members}");
    }

    private static Hash128 CandidateCalculationReceipt(
        Hash128 source,
        Hash128 context,
        Hash128 targetType,
        CircuitPairProposal proposal,
        Hash128 orchestration)
    {
        string basis = string.Join('|', proposal.BasisTypeIds
            .OrderBy(static id => id, Hash128BytewiseComparer.Instance));
        return Hash128.OfCanonical(
            $"{Family}/v{ModelTokenEdgeETL.AnalyzerVersion}/calculation/source={source}" +
            $"/context={context}/type={targetType}/subject={proposal.Subject}" +
            $"/object={proposal.Object}/basis={basis}/orchestration={orchestration}");
    }

    private static Hash128 OrchestrationReceipt(
        Hash128 first,
        Hash128 second,
        Hash128 targetType,
        IReadOnlyList<CircuitPairProposal> proposals)
    {
        if (first.CompareToBytewise(second) > 0) (first, second) = (second, first);
        string pairs = string.Join(';', proposals.Select(static proposal =>
            $"{proposal.Subject}>{proposal.Object}:" +
            string.Join(',', proposal.BasisTypeIds
                .OrderBy(static id => id, Hash128BytewiseComparer.Instance))));
        return Hash128.OfCanonical(
            $"{Family}/v{ModelTokenEdgeETL.AnalyzerVersion}/sources={first}|{second}" +
            $"/type={targetType}/pairs={pairs}");
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

    private static Dictionary<Hash128, int> IndexVocabulary(IReadOnlyList<Hash128> vocabulary)
    {
        var result = new Dictionary<Hash128, int>(vocabulary.Count);
        for (int i = 0; i < vocabulary.Count; i++) result.Add(vocabulary[i], i);
        return result;
    }

    private void Track(ModelCircuitEstate estate) =>
        PeakNativeResidentBytes = Math.Max(
            PeakNativeResidentBytes, estate.PeakNativeResidentBytes);

    private sealed record ModelTargetVote(
        Hash128 ContextId, long[] Scores, short[] Outcomes, int CircuitCount);

    private sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        public static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
