using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class NativeAttestation
{
    public readonly record struct CodepointRangeRelation(
        Hash128 TypeId,
        Hash128? ObjectId,
        Hash128? ContextId,
        bool Confirm = true,
        long ObservationCount = 1);

    public static unsafe List<int> CorroboratedIndexes(ReadOnlySpan<short> left, ReadOnlySpan<short> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Candidate outcome arrays must align.");
        byte[] admitted = new byte[left.Length];
        fixed (short* l = left)
        fixed (short* r = right)
        fixed (byte* output = admitted)
        {
            int rc = NativeInterop.AttestationCorroborationMask(l, r, (nuint)left.Length, output);
            if (rc != 0) throw new InvalidOperationException($"native corroboration admission failed: {rc}");
        }
        var indexes = new List<int>();
        for (int i = 0; i < admitted.Length; ++i)
            if (admitted[i] != 0) indexes.Add(i);
        return indexes;
    }

    public static unsafe (long Rating, long Rd) WitnessParameters(Hash128 typeId, double sourceTrust)
    {
        long rating, rd;
        int rc = NativeInterop.AttestationResolvedWitnessParameters(&typeId, sourceTrust, &rating, &rd);
        if (rc != 0) throw new InvalidOperationException($"native witness parameters failed: {rc}");
        return (rating, rd);
    }

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
            return WithSurfaceQualifier(ToRow(staged), surfaceRelation);
        }
    }

    // A relation surface may state a qualifier as well as an element and a direction
    // ("holo_member" is HAS_PART read backwards with meronymy/member); the claim keeps it.
    private static AttestationRow WithSurfaceQualifier(AttestationRow row, string surfaceRelation)
    {
        int bit = NativeInterop.RelationSurfaceQualifier(surfaceRelation);
        return bit < 0 ? row : row with { QualifierMask = row.QualifierMask.Set((byte)bit) };
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

    /// <summary>
    /// Produces a three-valued, native-oriented receipt for a relation the caller
    /// already resolved.  The receipt remains categorical.  A lane with a
    /// continuous native calculation must attach that value only as an atomic
    /// <see cref="EphemeralFoldInput"/>. This method marks the receipt
    /// non-replayable so it cannot later substitute 0/½/1 for that value.
    /// </summary>
    public static AttestationRow CategoricalResolvedOutcome(
        Hash128 subject,
        Hash128 typeId,
        Hash128? obj,
        Hash128 sourceId,
        Hash128? contextId,
        double witnessWeight,
        AttestationOutcome outcome,
        long observationCount = 1)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        unsafe
        {
            var staged = default(AttestationStagedNative);
            Hash128 objVal = obj ?? default;
            Hash128 ctxVal = contextId ?? default;
            int rc = NativeInterop.AttestationResolvedOutcomeBuild(
                &subject,
                &typeId,
                obj is null ? null : &objVal,
                (byte)(obj is null ? 1 : 0),
                &sourceId,
                contextId is null ? null : &ctxVal,
                (byte)(contextId is null ? 1 : 0),
                witnessWeight,
                (short)outcome,
                observationCount,
                0,
                &staged);
            if (rc != 0) throw new InvalidOperationException($"attestation build failed: {rc}");
            // The durable row is only the categorical receipt. Its continuous
            // calibration accompanies it through EphemeralFoldInput and is
            // consumed by the atomic consensus participant.
            return ToRow(staged) with { FoldReplayable = false };
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

    /// <summary>
    /// The governed UPOS label a source tag resolves to through its declared tagset
    /// (engine/manifest/vocabulary/pos_alias.tsv), or null when the tagset does not map it: an
    /// unmapped tag is the source's own value, never a guessed UPOS.
    /// </summary>
    private static readonly int[] NativeTagset = Enum.GetValues<PosReference.PosTagset>()
        .Select(static tagset => NativeInterop.PosTagsetFromName(tagset.ToString()) is var id and >= 0
            ? id : throw new InvalidOperationException($"native POS law has no tagset {tagset}"))
        .ToArray();

    public static string? ResolvePosCanonical(string tag, PosReference.PosTagset tagset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        unsafe
        {
            byte* canonical = null;
            int index;
            int rc = NativeInterop.PosResolveCanonical(tag, NativeTagset[(int)tagset], &canonical, &index);
            if (rc < 0) throw new InvalidOperationException($"pos resolve failed: {tag}");
            return rc == 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)canonical) : null;
        }
    }


    public static AttestationRow PosXpos(
        Hash128 subject, Hash128 xposEntity, Hash128 sourceId, Hash128? contextId,
        double sourceTrust, long observationCount = 1)
        => Categorical(subject, "HAS_XPOS", xposEntity, sourceId, contextId, sourceTrust,
            observationCount: observationCount);

    public static AttestationRow Aggregated(
        Hash128 subject, Hash128 typeId, Hash128? obj, Hash128 sourceId, Hash128? contextId,
        long games, long sumScoreFp1e9, double witnessWeight,
        long? opponentRatingFp1e9 = null)
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
            var row = ToRow(staged);
            return opponentRatingFp1e9 is { } opponentRating
                ? row with { OpponentRatingFp1e9 = opponentRating }
                : row;
        }
    }






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

    public static unsafe void AddCodepointRange(
        IntentStage stage,
        uint firstCodepoint,
        uint lastCodepoint,
        Hash128 typeId,
        Hash128? objectId,
        Hash128 sourceId,
        Hash128? contextId,
        double sourceTrust,
        bool confirm = true,
        long observationCount = 1)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Hash128 type = typeId, obj = objectId ?? default, source = sourceId;
        Hash128 context = contextId ?? default;
        int rc = NativeInterop.AttestationCodepointRangeAdd(
            stage.DangerousNativeHandle,
            firstCodepoint, lastCodepoint,
            &type, objectId is null ? null : &obj,
            (byte)(objectId is null ? 1 : 0), &source,
            contextId is null ? null : &context,
            (byte)(contextId is null ? 1 : 0),
            sourceTrust, confirm ? 1 : 0, observationCount);
        GC.KeepAlive(stage);
        if (rc != 0)
            throw new InvalidOperationException(
                $"native codepoint-range attestation staging failed (rc={rc}, range=U+{firstCodepoint:X4}..U+{lastCodepoint:X4})");
    }

    public static unsafe void AddCodepointRangeRelations(
        IntentStage stage,
        uint firstCodepoint,
        uint lastCodepoint,
        ReadOnlySpan<CodepointRangeRelation> relations,
        Hash128 sourceId,
        double sourceTrust)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (relations.IsEmpty) return;
        var native = new NativeInterop.CodepointRangeRelationNative[relations.Length];
        for (int i = 0; i < relations.Length; ++i)
        {
            CodepointRangeRelation relation = relations[i];
            if (relation.ObservationCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(relations));
            native[i] = new NativeInterop.CodepointRangeRelationNative
            {
                TypeId = relation.TypeId,
                ObjectId = relation.ObjectId ?? default,
                ContextId = relation.ContextId ?? default,
                ObjectIsNull = relation.ObjectId is null ? 1 : 0,
                ContextIsNull = relation.ContextId is null ? 1 : 0,
                Confirm = relation.Confirm ? 1 : 0,
                ObservationCount = relation.ObservationCount,
            };
        }

        fixed (NativeInterop.CodepointRangeRelationNative* request = native)
        {
            int rc = NativeInterop.AttestationCodepointRangeRelationsAdd(
                stage.DangerousNativeHandle,
                firstCodepoint,
                lastCodepoint,
                request,
                (nuint)native.Length,
                &sourceId,
                sourceTrust);
            GC.KeepAlive(stage);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"native codepoint range-relation staging failed (rc={rc}, range=U+{firstCodepoint:X4}..U+{lastCodepoint:X4}, relations={native.Length})");
        }
    }


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
            return WithSurfaceQualifier(ToRow(staged), surfaceRelation);
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
            s.OpponentRatingFp1e9,
            s.IsAggregated != 0 ? s.SumScoreFp1e9 : null,
            FoldReplayable: s.FoldReplayable != 0);
}
