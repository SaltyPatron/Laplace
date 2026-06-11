using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class NativeAttestation
{
    public static AttestationRow Categorical(
        Hash128 subject,
        string surfaceRelation,
        Hash128? obj,
        Hash128 sourceId,
        double sourceTrust,
        Hash128? contextId = null,
        bool confirm = true,
        long observationCount = 1)
        => Categorical(subject, surfaceRelation, obj, sourceId, contextId, sourceTrust, confirm, observationCount);

    public static AttestationRow Categorical(
        Hash128 subject,
        string surfaceRelation,
        Hash128? obj,
        Hash128 sourceId,
        double sourceTrust,
        double magnitude,
        double arenaScale,
        Hash128? contextId = null,
        long observationCount = 1)
        => BuildCategoricalScored(subject, surfaceRelation, obj, sourceId, contextId, sourceTrust,
            magnitude, arenaScale, observationCount);

    public static AttestationRow Categorical(
        Hash128 subject,
        string surfaceRelation,
        Hash128? obj,
        Hash128 sourceId,
        Hash128? contextId,
        double sourceTrust,
        bool confirm = true,
        long observationCount = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceRelation);
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationCategoricalBuild(
                surfaceRelation,
                &subject,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                sourceTrust,
                confirm ? 1 : 0,
                observationCount,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            return ToRow(staged);
        }
    }

    public static AttestationRow CategoricalResolved(
        Hash128 subject,
        Hash128 typeId,
        Hash128? obj,
        Hash128 sourceId,
        Hash128? contextId,
        double witnessWeight,
        bool confirm = true,
        long observationCount = 1)
    {
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationResolvedBuild(
                &subject,
                &typeId,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                witnessWeight,
                confirm ? 1 : 0,
                observationCount,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            return ToRow(staged);
        }
    }

    public static AttestationRow ResolvedScored(
        Hash128 subject,
        Hash128 typeId,
        Hash128? obj,
        Hash128 sourceId,
        Hash128? contextId,
        double witnessWeight,
        double signedMagnitude,
        double arenaScale,
        long observationCount = 1)
    {
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationResolvedScoredBuild(
                &subject,
                &typeId,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                witnessWeight,
                signedMagnitude,
                arenaScale,
                observationCount,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            return ToRow(staged);
        }
    }

    public static Hash128 ComputeId(
        Hash128 subject, Hash128 typeId, Hash128? obj,
        Hash128 sourceId, Hash128? contextId)
        => CategoricalResolved(subject, typeId, obj, sourceId, contextId, 1.0).Id;

    public static Hash128 ResolvePos(string tag, PosTagset tagset) =>
        tagset switch
        {
            PosTagset.Upos => ResolvePosNative(tag, 0),
            PosTagset.WordNet => ResolvePosNative(tag, 1),
            PosTagset.Wiktionary => ResolvePosNative(tag, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(tagset)),
        };

    public static AttestationRow PosUpos(
        Hash128 subject, string uposTag, Hash128 sourceId, Hash128? contextId,
        double sourceTrust, long observationCount = 1)
        => Categorical(subject, "HAS_UPOS", ResolvePos(uposTag, PosTagset.Upos), sourceId, contextId, sourceTrust,
            observationCount: observationCount);

    public static AttestationRow PosXpos(
        Hash128 subject, Hash128 xposEntity, Hash128 sourceId, Hash128? contextId,
        double sourceTrust, long observationCount = 1)
        => Categorical(subject, "HAS_XPOS", xposEntity, sourceId, contextId, sourceTrust,
            observationCount: observationCount);

    public static AttestationRow PosWordNet(
        Hash128 subject, char ssType, Hash128 sourceId, Hash128? contextId,
        double sourceTrust, long observationCount = 1)
        => PosUpos(subject, ssType.ToString(), sourceId, contextId, sourceTrust, observationCount);

    public static AttestationRow PosWiktionary(
        Hash128 subject, string pos, Hash128 sourceId, Hash128? contextId,
        double sourceTrust, long observationCount = 1)
    {
        Hash128 posId = ResolvePos(pos, PosTagset.Wiktionary);
        return Categorical(subject, "HAS_POS", posId, sourceId, contextId, sourceTrust, observationCount: observationCount);
    }

    public static AttestationRow Aggregated(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        long games, long sumScoreFp1e9, double witnessWeight)
    {
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationAggregatedBuild(
                &subject,
                &typeId,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                witnessWeight,
                games,
                sumScoreFp1e9,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            return ToRow(staged);
        }
    }

    /// <summary>
    /// Batch form of <see cref="Aggregated"/> for arenas where every cell shares
    /// (type, source, context, weight): one P/Invoke per chunk instead of one per cell.
    /// Fills <paramref name="staged"/>[0..count) — convert rows via <see cref="Row"/>.
    /// </summary>
    public static void AggregatedBatch(
        AttestationAggregatedCellNative[] cells, int count,
        Hash128 typeId, Hash128 sourceId, Hash128? contextId, double witnessWeight,
        AttestationStagedNative[] staged)
    {
        if (count == 0) return;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, cells.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, staged.Length);
        unsafe
        {
            Hash128 ctxVal = contextId ?? default;
            fixed (AttestationAggregatedCellNative* pc = cells)
            fixed (AttestationStagedNative* ps = staged)
            {
                int rc = NativeInterop.AttestationAggregatedBatchBuild(
                    pc, (nuint)count, &typeId, &sourceId,
                    contextId is null ? null : &ctxVal,
                    (byte)(contextId is null ? 1 : 0),
                    witnessWeight, 0, ps);
                if (rc != 0)
                    throw new InvalidOperationException($"aggregated batch build failed: {rc}");
            }
        }
    }

    public static AttestationRow Row(in AttestationStagedNative staged) => ToRow(staged);

    /// <summary>Rational Score law over a float span — one P/Invoke per call.</summary>
    public static void ScoreBatchFp(ReadOnlySpan<float> values, double arenaScale, Span<long> outFp)
    {
        if (values.Length == 0) return;
        ArgumentOutOfRangeException.ThrowIfLessThan(outFp.Length, values.Length);
        unsafe
        {
            fixed (float* pv = values)
            fixed (long* po = outFp)
                NativeInterop.ScoreBatchFp(pv, (nuint)values.Length, arenaScale, po);
        }
    }

    public static double WitnessPhi(double witnessWeight) =>
        NativeInterop.AttestationWitnessPhi(witnessWeight);

    public static long ScoreFp(double signedMagnitude, double arenaScale) =>
        NativeInterop.ScoreFp(signedMagnitude, arenaScale);

    public static double Score(double signedMagnitude, double arenaScale) =>
        ScoreFp(signedMagnitude, arenaScale) / (double)Glicko2.FpScale;

    public enum PosTagset { Upos, WordNet, Wiktionary }

    private static AttestationRow BuildCategoricalScored(
        Hash128 subject,
        string surfaceRelation,
        Hash128? obj,
        Hash128 sourceId,
        Hash128? contextId,
        double sourceTrust,
        double magnitude,
        double arenaScale,
        long observationCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceRelation);
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationCategoricalScoredBuild(
                surfaceRelation,
                &subject,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                sourceTrust,
                magnitude,
                arenaScale,
                observationCount,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            return ToRow(staged);
        }
    }

    private static Hash128 ResolvePosNative(string tag, int tagset)
    {
        unsafe
        {
            Hash128 id;
            int rc = NativeInterop.PosResolveEntity(tag, tagset, &id);
            if (rc < 0) throw new InvalidOperationException($"pos resolve failed: {tag}");
            return id;
        }
    }

    private static AttestationRow ToRow(in AttestationStagedNative s) =>
        new(
            s.Id,
            s.SubjectId,
            s.TypeId,
            s.ObjectIsNull != 0 ? null : s.ObjectId,
            s.SourceId,
            s.ContextIsNull != 0 ? null : s.ContextId,
            (AttestationOutcome)s.Outcome,
            s.LastObservedAtUnixUs,
            s.ObservationCount,
            s.ScoreFp1e9,
            s.OpponentRdFp1e9,
            s.IsAggregated != 0 ? s.SumScoreFp1e9 : null);
}
