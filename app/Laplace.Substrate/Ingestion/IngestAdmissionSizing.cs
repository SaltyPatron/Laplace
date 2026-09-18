using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Known source-local payload retained while canonical physicality provenance is
/// recorded. Entity identity and native trajectories already carry the Merkle
/// structure; this envelope never budgets a parallel descriptor graph.
/// </summary>
internal readonly record struct IngestAdmissionSizing(
    long SerializedBytes,
    PhysicalityDescriptorSizing.Shape Source,
    PhysicalityDescriptorSizing.Shape Admitted,
    long SourceTupleBytes,
    long AdmittedTupleBytes,
    long SourceStages,
    long AdmittedStages,
    long CaptureReservationBytes = 0,
    long HeldNativeBytes = 0)
{
    internal IngestAdmissionSizing Add(IngestAdmissionSizing next) => new(
        checked(SerializedBytes + next.SerializedBytes),
        Source.Add(next.Source),
        Admitted.Add(next.Admitted),
        checked(SourceTupleBytes + next.SourceTupleBytes),
        checked(AdmittedTupleBytes + next.AdmittedTupleBytes),
        checked(SourceStages + next.SourceStages),
        checked(AdmittedStages + next.AdmittedStages),
        checked(CaptureReservationBytes + next.CaptureReservationBytes),
        checked(HeldNativeBytes + next.HeldNativeBytes));

    internal long ModeledSourcePayloadBytes
    {
        get
        {
            if (Source.Forms == 0) return Math.Max(SerializedBytes, HeldNativeBytes);
            checked
            {
                long forms = (long)Source.Forms;
                // Client: physicality/entity/source/unit ids + timestamp.
                long metadata = forms * (4 * 16L + sizeof(long));
                // One set-sized PostgreSQL write receives four bytea arrays and
                // one int8 array. Account element varlena headers, array headers,
                // Npgsql reference vectors and the retained client metadata.
                long sqlArrays = 5L * 24
                    + forms * (4L * (4 + 16) + sizeof(long))
                    + forms * 5L * IntPtr.Size;
                return Math.Max(SerializedBytes,
                    HeldNativeBytes + metadata + sqlArrays + CaptureReservationBytes);
            }
        }
    }

    internal static IngestAdmissionSizing Measure(
        SubstrateChange change, long serializedBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ct.ThrowIfCancellationRequested();
        var stages = change.IntentStages.IsDefaultOrEmpty
            ? Array.Empty<IntentStage>()
            : change.IntentStages.Where(stage => !stage.IsInvalid).ToArray();
        return MeasureParts(serializedBytes, stages,
            ShapeOf(change.Physicalities, ct),
            ShapeOf(change.PhysicalityObservations, ct),
            (ulong)change.Entities.Length, (ulong)change.Attestations.Length,
            growingStages: false, ct);
    }

    // The same model consumes either a completed intent's exact native shape or a
    // growing builder's O(1)-per-stage conservative shape. Managed dimensions are
    // accumulated on append by the builder; no growing trajectory list is rescanned.
    internal static IngestAdmissionSizing MeasureParts(
        long serializedBytes, IReadOnlyList<IntentStage> stages,
        PhysicalityDescriptorSizing.Shape selected,
        PhysicalityDescriptorSizing.Shape raw,
        ulong entities, ulong attestations, bool growingStages,
        CancellationToken ct = default) =>
        MeasurePartsCore(serializedBytes, stages, selected, raw, entities, attestations,
            growingStages, selectedRowsAlreadyObserved: false, ct);

    // AddPhysicality appends every row to raw before retaining its selected
    // reference. This invariant belongs to the builder, not arbitrary changes.
    internal static IngestAdmissionSizing MeasureGrowingBuilder(
        long serializedBytes, IReadOnlyList<IntentStage> stages,
        PhysicalityDescriptorSizing.Shape selected,
        PhysicalityDescriptorSizing.Shape raw,
        ulong entities, ulong attestations) =>
        MeasurePartsCore(serializedBytes, stages, selected, raw, entities, attestations,
            growingStages: true, selectedRowsAlreadyObserved: true, default);

    private static IngestAdmissionSizing MeasurePartsCore(
        long serializedBytes, IReadOnlyList<IntentStage> stages,
        PhysicalityDescriptorSizing.Shape selected,
        PhysicalityDescriptorSizing.Shape raw,
        ulong entities, ulong attestations, bool growingStages,
        bool selectedRowsAlreadyObserved, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var native = growingStages
            ? default(PhysicalityDescriptorSizing.Shape)
            : PhysicalityDescriptorSizing.FromStages(stages);
        long sourceTuples = 0, admittedTuples = 0, sourceStages = 0, admittedStages = 0;
        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();
            if (stage.IsInvalid) continue;
            if (growingStages) native = native.Add(PhysicalityDescriptorSizing.FromStageBound(stage));
            long tuples = stage.TotalTupleBytes;
            admittedTuples = checked(admittedTuples + tuples);
            admittedStages++;
            if (stage.PhysicalityCount != 0)
            {
                sourceTuples = checked(sourceTuples + tuples);
                sourceStages++;
            }
        }

        // Arbitrary changes may supplement raw with selected row references.
        // A growing builder has already appended every selected row to raw:
        // capture emits raw exactly, including repeated and distinct same-ID rows.
        var managedSource = selectedRowsAlreadyObserved
            ? raw
            : raw.Forms == 0 ? selected : raw.Add(selected);
        long captureReservation = 0;
        if (selectedRowsAlreadyObserved && raw.Forms != 0 && selected.Forms != 0)
        {
            // The builder already records source-order references. Only the
            // selected-reference set used to avoid repeating the same object is
            // additional to the direct provenance arrays.
            captureReservation = checked((long)selected.Forms * IntPtr.Size);
        }
        if (managedSource.Forms != 0)
        {
            sourceTuples = checked(sourceTuples + PhysicalityDescriptorSizing.TuplePayloadBound(
                0, managedSource.Forms, managedSource.StoredVertices, 0));
            sourceStages++;
        }
        bool managed = entities != 0 || selected.Forms != 0 || attestations != 0;
        admittedTuples = checked(admittedTuples + PhysicalityDescriptorSizing.TuplePayloadBound(
            entities, selected.Forms, selected.StoredVertices, attestations));
        return new(serializedBytes, native.Add(managedSource), native.Add(selected),
            sourceTuples, admittedTuples, sourceStages, checked(admittedStages + (managed ? 1L : 0)),
            captureReservation, HeldNativeStageBytes(stages));
    }

    // These are the borrowed producer stages, which stay alive alongside the
    // separate transport/capture/plan allocations above. Native accounting includes
    // auxiliary interpretation capacity and witness storage without row guesses.
    internal static long HeldNativeStageBytes(IEnumerable<IntentStage> stages)
    {
        long bytes = 0;
        var seen = new HashSet<IntentStage>(ReferenceEqualityComparer.Instance);
        foreach (var stage in stages)
            if (!stage.IsInvalid && seen.Add(stage))
                bytes = checked(bytes + stage.AllocatedBytes);
        return bytes;
    }

    private static PhysicalityDescriptorSizing.Shape ShapeOf(
        System.Collections.Immutable.ImmutableArray<PhysicalityRow> rows, CancellationToken ct)
    {
        if (rows.IsDefaultOrEmpty) return default;
        ulong vertices = 0, widest = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            int values = row.TrajectoryXyzm?.Length ?? 0;
            if (values % 4 != 0)
                throw new InvalidOperationException("physicality observation contains a partial trajectory vertex");
            ulong count = (ulong)(values / 4);
            vertices = checked(vertices + count);
            widest = Math.Max(widest, count);
        }
        return new((ulong)rows.Length, vertices, widest);
    }
}

/// <summary>
/// Groups existing complete intents before apply. It never changes an intent or
/// retries an apply. An over-bound singleton still reaches the actual native
/// grant: a conservative no-reuse estimate is not evidence that it cannot fit.
/// </summary>
internal sealed class IngestAdmissionWindow(long maximumModeledBytes)
{
    internal long MaximumModeledBytes { get; } = maximumModeledBytes > 0
        ? maximumModeledBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumModeledBytes));
    internal int Count { get; private set; }
    internal IngestAdmissionSizing Sizing { get; private set; }
    internal long ModeledSourcePayloadBytes => Sizing.ModeledSourcePayloadBytes;

    internal bool ShouldFlushBefore(IngestAdmissionSizing next) =>
        Count != 0 && Sizing.Add(next).ModeledSourcePayloadBytes > MaximumModeledBytes;

    internal void Add(IngestAdmissionSizing next)
    {
        if (ShouldFlushBefore(next))
            throw new InvalidOperationException("admission window must drain before another complete intent");
        Sizing = Sizing.Add(next);
        Count = checked(Count + 1);
    }

    internal void Reset()
    {
        Count = 0;
        Sizing = default;
    }
}
