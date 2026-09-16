using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Ingestion.Tests;

/// <summary>Actual native shapes/plans and unchanged complete-intent grouping.
/// These controls do not simulate database persistence or provider closure.</summary>
[Collection("GrammarPerfcache")]
public sealed class IngestAdmissionSizingTests
{
    private static readonly Hash128 Source = Hash128.Blake3("admission-window-test-source"u8);

    [Fact]
    public void NativeShapesAndRawObservationBoundIncludeReusedAndCopiedRows()
    {
        using var stage = IntentStage.New(0);
        var row = Row(1);
        Append(stage, row);
        Append(stage, row with { CoordX = .25 });
        var shape = PhysicalityDescriptorSizing.FromStages([stage]);
        Assert.Equal(2UL, shape.Forms);
        Assert.Equal(4UL, shape.StoredVertices);
        Assert.Equal(2UL, shape.MaximumVertices);
        Assert.True(PhysicalityDescriptorSizing.TuplePayloadBound(0, shape.Forms,
            shape.StoredVertices, 0) >= stage.TupleBuffer(IntentStageTable.Physicalities).Len);

        using var builder = new SubstrateChangeBuilder(Source, "raw-observation-window");
        builder.AddPhysicality(row).AddPhysicality(row).AddPhysicality(row with { });
        var change = builder.Build();
        Assert.Single(change.Physicalities);
        Assert.Equal(3, change.PhysicalityObservations.Length);
        var sizing = IngestAdmissionSizing.Measure(change, 152);
        // Capture reserves the raw+selected reference union before it resolves
        // exact shared references; sizing retains that conservative contract.
        Assert.Equal(4UL, sizing.Source.Forms);
        Assert.Equal(8UL, sizing.Source.StoredVertices);
        Assert.Equal(1UL, sizing.Admitted.Forms);
        Assert.True(sizing.ModeledSourcePayloadBytes > sizing.SerializedBytes);
        Assert.Same(row, change.PhysicalityObservations[0]);
        Assert.Same(row, change.PhysicalityObservations[1]);
        Assert.NotSame(row, change.PhysicalityObservations[2]);
    }

    [Fact]
    public void WholeIntentWindowsPreserveIdsSourcesObservationsAndNativePayloads()
    {
        var changes = Enumerable.Range(1, 9).Select(Change).ToArray();
        var sizes = changes.Select(change => IngestAdmissionSizing.Measure(change, 152)).ToArray();
        long grant = sizes[0].Add(sizes[1]).ModeledSourcePayloadBytes;
        var groups = Group(changes, sizes, grant);
        Assert.InRange(groups.Count, 2, changes.Length);
        var flattened = groups.SelectMany(group => group).ToArray();
        Assert.Equal(changes.Length, flattened.Length);
        for (int i = 0; i < changes.Length; i++)
        {
            Assert.Same(changes[i], flattened[i]);
            Assert.Equal(changes[i].Metadata, flattened[i].Metadata);
            Assert.Same(changes[i].PhysicalitySourcePriors, flattened[i].PhysicalitySourcePriors);
            Assert.Equal(3, flattened[i].PhysicalityObservations.Length);
        }
        Assert.Equal(27, flattened.Sum(change => change.PhysicalityObservations.Length));
        foreach (var group in groups)
        {
            var total = group.Aggregate(default(IngestAdmissionSizing),
                (value, change) => value.Add(IngestAdmissionSizing.Measure(change, 152)));
            Assert.True(total.ModeledSourcePayloadBytes <= grant);
        }

        using var whole = Scalar(changes.SelectMany(c => c.PhysicalityObservations));
        var groupedTuples = new List<byte>();
        foreach (var group in groups)
        {
            using var native = Scalar(group.SelectMany(c => c.PhysicalityObservations));
            groupedTuples.AddRange(Tuples(native));
        }
        Assert.Equal(Tuples(whole), groupedTuples.ToArray());
    }

    [Fact]
    public unsafe void NativeCaptureAndDescriptorPlanFitTheirModeledFiniteWindow()
    {
        var changes = Enumerable.Range(1, 4).Select(Change).ToArray();
        var size = changes.Select(change => IngestAdmissionSizing.Measure(change, 152))
            .Aggregate(default(IngestAdmissionSizing), (left, right) => left.Add(right));
        var rows = changes.SelectMany(change => change.PhysicalityObservations).ToArray();
        using var stage = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
            rows, size.ModeledSourcePayloadBytes, 200);
        var shape = PhysicalityDescriptorSizing.FromStages([stage]);
        Assert.Equal((ulong)rows.Length, shape.Forms);
        long grant = checked(shape.CapturePayloadBound + shape.PlanPayloadBound);
        IntPtr vocabulary = IntPtr.Zero, capture = IntPtr.Zero;
        lock (LaplaceCoreGate.Native)
        {
            try
            {
                var source = Source;
                Assert.Equal(0, VocabularyCreate(&source,
                    checked((nuint)IngestSizing.ResolveWorkingSetBudgetBytes()), &vocabulary));
                Assert.NotEqual(IntPtr.Zero, vocabulary);
                IntPtr basis = VocabularyBasis(vocabulary);
                Assert.NotEqual(IntPtr.Zero, basis);
                IntPtr handle = stage.DangerousGetHandle();
                nuint planLimit = checked((nuint)shape.PlanPayloadBound);
                Assert.Equal(-2, CaptureStages(&handle, 1, basis, &planLimit, 1, &capture));
                Assert.Equal(IntPtr.Zero, capture);
                Assert.Equal(0, CaptureStages(&handle, 1, basis, &planLimit,
                    checked((nuint)grant), &capture));
                Assert.NotEqual(IntPtr.Zero, capture);
                Assert.True((ulong)CapturePeak(capture) <= (ulong)grant);
                nuint count = 0;
                Assert.NotEqual(IntPtr.Zero, CaptureInputs(capture, &count));
                Assert.Equal((ulong)rows.Length, (ulong)count);
                Assert.Equal(rows.Length, stage.PhysicalityCount);
                GC.KeepAlive(stage);
            }
            finally
            {
                CaptureFree(capture);
                VocabularyFree(vocabulary);
            }
        }
    }

    [Fact]
    public void ConservativeOverBoundSingletonReachesActualOwnerIntact()
    {
        var change = Change(1);
        var size = IngestAdmissionSizing.Measure(change, 152);
        var window = new IngestAdmissionWindow(size.ModeledSourcePayloadBytes - 1);
        Assert.False(window.ShouldFlushBefore(size));
        window.Add(size);
        Assert.Equal(1, window.Count);
        Assert.True(window.ModeledSourcePayloadBytes > window.MaximumModeledBytes);
        Assert.True(window.ShouldFlushBefore(size));
        Assert.Throws<InvalidOperationException>(() => window.Add(size));
        window.Reset();
        Assert.False(window.ShouldFlushBefore(size));
        window.Add(size);
        Assert.Equal(size, window.Sizing);
        Assert.Equal(3, change.PhysicalityObservations.Length);
    }

    [Fact]
    public void NativeStageSizingDoesNotTakeOwnershipAndRefusesClosedStages()
    {
        var stage = Scalar([Row(1)]);
        byte[] before = Tuples(stage);
        Assert.Equal(1UL, PhysicalityDescriptorSizing.FromStages([stage]).Forms);
        Assert.False(stage.IsClosed);
        Assert.Equal(before, Tuples(stage));
        stage.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PhysicalityDescriptorSizing.FromStages([stage]));
    }

    [Fact]
    public void SizingCancellationAndMalformedTrajectoryDoNotMutateTheIntent()
    {
        var change = Change(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            IngestAdmissionSizing.Measure(change, 152, cancelled.Token));
        Assert.Equal(3, change.PhysicalityObservations.Length);
        var invalid = Row(2) with { TrajectoryXyzm = [1, 2, 3] };
        var malformed = change with { PhysicalityObservations = [invalid] };
        Assert.Throws<InvalidOperationException>(() => IngestAdmissionSizing.Measure(malformed, 152));
        Assert.Same(invalid, Assert.Single(malformed.PhysicalityObservations));
        Assert.True(IngestAdmissionSizing.Measure(change, 152).ModeledSourcePayloadBytes > 152);
    }

    [Fact]
    public void GrowingBuilderUsesNativeWireBoundAndIncrementalRawDimensionsWithoutChangingOwnership()
    {
        using var builder = new SubstrateChangeBuilder(Source, "growing-admission-window");
        Assert.Equal(0L, SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder));
        var row = Row(1);
        builder.AddPhysicality(row).AddPhysicality(row).AddPhysicality(row with { });
        long managed = SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder);
        using var stage = IntentStage.New(0);
        Append(stage, Row(2));
        builder.AddIntentStage(stage);
        var exactShape = PhysicalityDescriptorSizing.FromStages([stage]);
        var wireShape = PhysicalityDescriptorSizing.FromStageBound(stage);
        Assert.Equal(exactShape.Forms, wireShape.Forms);
        Assert.True(wireShape.StoredVertices >= exactShape.StoredVertices);
        Assert.True(wireShape.MaximumVertices >= exactShape.MaximumVertices);
        byte[] before = Tuples(stage);
        long first = SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder);
        Assert.True(first > managed);
        Assert.Equal(before, Tuples(stage));
        Append(stage, Row(3));
        long second = SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder);
        Assert.True(second > first);
        long serialized = builder.StagedBytesEstimate;
        var change = builder.Build();
        try
        {
            Assert.True(second >= IngestAdmissionSizing.Measure(change, serialized).ModeledSourcePayloadBytes);
            Assert.Equal(3, change.PhysicalityObservations.Length);
            Assert.Single(change.Physicalities);
            Assert.False(stage.IsClosed);
            Assert.Equal(2, stage.PhysicalityCount);
            // Build transfers native stages, while the builder's managed rows remain.
            // Their incremental counters must remain too if the builder is queried again.
            Assert.Equal(managed, SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder));
            builder.Dispose();
            Assert.False(stage.IsClosed);
            Assert.Throws<ObjectDisposedException>(() =>
                SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder));
        }
        finally { foreach (var owned in change.IntentStages) owned.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => PhysicalityDescriptorSizing.FromStageBound(stage));
    }

    [Fact]
    public void DefaultRawSidecarAndMalformedGrowingTrajectoryKeepExistingAdmissionSemantics()
    {
        var valid = Change(1) with { PhysicalityObservations = default };
        Assert.Equal((ulong)valid.Physicalities.Length, IngestAdmissionSizing.Measure(valid, 152).Source.Forms);
        using var builder = new SubstrateChangeBuilder(Source, "partial-trajectory-window");
        var invalid = Row(1) with { TrajectoryXyzm = [1, 2, 3] };
        builder.AddPhysicality(invalid);
        Assert.Throws<InvalidOperationException>(() =>
            SubstrateChangeBuilder.ModeledSourceAdmissionPayloadBytes(builder));
        var change = builder.Build();
        Assert.Same(invalid, Assert.Single(change.PhysicalityObservations));
        Assert.Throws<InvalidOperationException>(() => IngestAdmissionSizing.Measure(change, 152));
    }

    private static List<List<SubstrateChange>> Group(
        SubstrateChange[] changes, IngestAdmissionSizing[] sizes, long grant)
    {
        var window = new IngestAdmissionWindow(grant);
        var groups = new List<List<SubstrateChange>>();
        var pending = new List<SubstrateChange>();
        for (int i = 0; i < changes.Length; i++)
        {
            if (window.ShouldFlushBefore(sizes[i]))
            {
                groups.Add(pending);
                pending = [];
                window.Reset();
            }
            window.Add(sizes[i]);
            pending.Add(changes[i]);
        }
        if (pending.Count != 0) groups.Add(pending);
        return groups;
    }

    private static SubstrateChange Change(int number)
    {
        using var builder = new SubstrateChangeBuilder(Source, $"window-unit-{number}");
        var row = Row(number);
        builder.AddPhysicality(row).AddPhysicality(row).AddPhysicality(row with { CoordX = .25 });
        return builder.SetInputUnitsConsumed(1).Build().WithSourcePrior(Source, .75);
    }

    private static PhysicalityRow Row(int number)
    {
        var entity = new Hash128((ulong)number, 0x1122_3344_5566_7788);
        double[] coordinates = [.1, .2, .3, .4];
        return new(PhysicalityId.Compute(entity, PhysicalityType.Projection), entity,
            Source, PhysicalityType.Projection, coordinates[0], coordinates[1],
            coordinates[2], coordinates[3], Hilbert128.Encode(coordinates),
            Trajectory.Build([Source, Source, entity]), 3, null, null,
            IntentStage.PgEpochUnixUs + number);
    }

    private static IntentStage Scalar(IEnumerable<PhysicalityRow> rows)
    {
        var stage = IntentStage.New(0);
        try
        {
            foreach (var row in rows) Append(stage, row);
            return stage;
        }
        catch { stage.Dispose(); throw; }
    }

    private static void Append(IntentStage stage, PhysicalityRow row) =>
        stage.AddPhysicality(row.Id, row.EntityId, (short)row.Type,
            [row.CoordX, row.CoordY, row.CoordZ, row.CoordM], row.HilbertIndex,
            row.TrajectoryXyzm ?? [], row.NConstituents,
            row.AlignmentResidual, row.SourceDim, row.ObservedAtUnixUs);

    private static byte[] Tuples(IntentStage stage)
    {
        var (pointer, length) = stage.TupleBuffer(IntentStageTable.Physicalities);
        var bytes = new byte[checked((int)length)];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        GC.KeepAlive(stage);
        return bytes;
    }

    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_vocabulary_create")]
    private static extern unsafe int VocabularyCreate(Hash128* source, nuint grant, IntPtr* output);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_vocabulary_basis")]
    private static extern IntPtr VocabularyBasis(IntPtr value);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_vocabulary_free")]
    private static extern void VocabularyFree(IntPtr value);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_capture_stages")]
    private static extern unsafe int CaptureStages(
        IntPtr* stages, nuint count, IntPtr basis, nuint* limits, nuint grant, IntPtr* output);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_capture_peak_bytes")]
    private static extern nuint CapturePeak(IntPtr value);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_capture_inputs")]
    private static extern unsafe IntPtr CaptureInputs(IntPtr value, nuint* count);
    [DllImport("laplace_core", EntryPoint = "physicality_descriptor_capture_free")]
    private static extern void CaptureFree(IntPtr value);
}
