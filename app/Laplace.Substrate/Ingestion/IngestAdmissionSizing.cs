using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Known source-local payload that the physicality admission owners must retain
/// together. Descriptor recipes, tuple widths and capture layout stay native.
/// This predicts neither provider closure nor complete admission: fixed SQL and
/// vocabulary/floor state, imported-buffer spare capacity, allocator overhead,
/// elected views and provider materialization remain under the real runtime grant.
/// </summary>
internal readonly record struct IngestAdmissionSizing(
    long SerializedBytes,
    PhysicalityDescriptorSizing.Shape Source,
    PhysicalityDescriptorSizing.Shape Admitted,
    long SourceTupleBytes,
    long AdmittedTupleBytes,
    long SourceStages,
    long AdmittedStages,
    long CaptureReservationBytes = 0)
{
    internal IngestAdmissionSizing Add(IngestAdmissionSizing next) => new(
        checked(SerializedBytes + next.SerializedBytes),
        Source.Add(next.Source),
        Admitted.Add(next.Admitted),
        checked(SourceTupleBytes + next.SourceTupleBytes),
        checked(AdmittedTupleBytes + next.AdmittedTupleBytes),
        checked(SourceStages + next.SourceStages),
        checked(AdmittedStages + next.AdmittedStages),
        checked(CaptureReservationBytes + next.CaptureReservationBytes));

    internal long ModeledSourcePayloadBytes
    {
        get
        {
            if (Source.Forms == 0) return SerializedBytes;
            checked
            {
                long forms = (long)Source.Forms;
                long stages = SourceStages + AdmittedStages;
                // NpgsqlPhysicalityAdmission reserves two IDs and one double per
                // source observation, then exports another aligned parameter set.
                long metadata = forms * (2 * 16L + sizeof(double));
                long tupleBytes = SourceTupleBytes + AdmittedTupleBytes;
                long clientTransport = metadata + forms * (2L * IntPtr.Size)
                    + tupleBytes + stages * (3L * IntPtr.Size);

                // Nine flat non-null PostgreSQL arrays: six stage bytea arrays,
                // two 16-byte identity arrays and one float8 array. A nonempty
                // array header is 24 bytes; each bytea has four header bytes and
                // at most three alignment bytes. Empty arrays fit that upper bound.
                // admission_array also retains Datum and bool deconstruction arrays.
                long sqlArrays = 9L * 24
                    + tupleBytes + stages * (3L * (4 + 3))
                    + forms * (2L * (4 + 16) + sizeof(double))
                    + (3L * stages + 3L * forms) * (IntPtr.Size + sizeof(bool));
                long sqlSourceMetadata = metadata;
                // Encoded source/admitted payload coexists during capture.
                // This is payload, not spare native capacity or allocator RSS.
                long encodedPayload = tupleBytes;
                long captures = Source.CapturePayloadBound + Admitted.CapturePayloadBound;
                // The later source-local plan may contain both source and admitted
                // bodies. Its no-reuse upper bound also covers original-only validation.
                long plan = Source.Add(Admitted).PlanPayloadBound;
                return Math.Max(SerializedBytes, metadata + clientTransport
                    + sqlArrays + sqlSourceMetadata + encodedPayload + captures + plan
                    + CaptureReservationBytes);
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
            // Capture still reserves raw+selected metadata capacity before its
            // reference union, plus a possible merged reference array and the
            // selected-reference set. Retain those actual client reservations;
            // only the duplicated source tuple/capture/descriptor bodies vanish.
            captureReservation = checked((long)selected.Forms * 40L
                + ((long)raw.Forms + 2L * (long)selected.Forms) * IntPtr.Size);
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
            captureReservation);
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
