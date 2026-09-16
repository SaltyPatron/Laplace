using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Executes the actual managed/native transport. Native stages are the wire
/// oracle; no PostgreSQL server or successful persistence is implied here.
/// </summary>
public class PhysicalityAdmissionTransportTests
{
    private const long Grant = 4 * 1024 * 1024;
    private static Hash128 H(int value) => new((ulong)value, 0x1020_3040_5060_7080ul);

    [Fact]
    public unsafe void PhysicalityInput_UsesExact112ByteNativeLayout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(112, sizeof(PhysicalityDescriptorInputNative));
        Assert.Equal(112, Marshal.SizeOf<PhysicalityDescriptorInputNative>());
        var fields = new (string Name, int Offset)[]
        {
            (nameof(PhysicalityDescriptorInputNative.EntityId), 0),
            (nameof(PhysicalityDescriptorInputNative.Type), 16),
            (nameof(PhysicalityDescriptorInputNative.Coordinate), 24),
            (nameof(PhysicalityDescriptorInputNative.HilbertIndex), 56),
            (nameof(PhysicalityDescriptorInputNative.Trajectory), 72),
            (nameof(PhysicalityDescriptorInputNative.TrajectoryVertices), 80),
            (nameof(PhysicalityDescriptorInputNative.Constituents), 88),
            (nameof(PhysicalityDescriptorInputNative.AlignmentResidualIsNull), 92),
            (nameof(PhysicalityDescriptorInputNative.AlignmentResidual), 96),
            (nameof(PhysicalityDescriptorInputNative.SourceDimIsNull), 104),
            (nameof(PhysicalityDescriptorInputNative.SourceDim), 108),
        };
        foreach (var (name, offset) in fields)
            Assert.Equal(offset, Marshal.OffsetOf<PhysicalityDescriptorInputNative>(name).ToInt32());
    }

    [Fact]
    public unsafe void PhysicalityBatch_MatchesNativeScalarWireWithRleAndNullableFields()
    {
        double[] trajectory = Trajectory.Build([H(1), H(1), H(2)]);
        Assert.Equal(8, trajectory.Length); // One stored run plus the next child.
        Hash128 entity = Trajectory.ContentIdentity(trajectory, out int count);
        Assert.Equal(3, count);
        double[] firstCoord = [.1, -0.0, .3, -.4];
        double[] secondCoord = [.2, .05, -.125, .25];
        long[] times = [IntentStage.PgEpochUnixUs - 1_234_567, 1_700_123_456_789_123];
        Hash128[] ids = [PhysicalityId.Compute(entity, PhysicalityType.Content),
            PhysicalityId.Compute(H(9), PhysicalityType.Projection)];
        using var batch = IntentStage.NewBounded(0, Grant);
        using var scalar = IntentStage.New(0);
        fixed (double* packed = trajectory)
        {
            PhysicalityDescriptorInputNative[] inputs =
            [
                Input(entity, PhysicalityType.Content, firstCoord, packed, 2, 3, .375, 7),
                Input(H(9), PhysicalityType.Projection, secondCoord, null, 0, 0, null, null),
            ];
            // Null markers, rather than the unused scalar slots, own nullness.
            inputs[1].AlignmentResidual = 987.5;
            inputs[1].SourceDim = 999;
            batch.AddPhysicalityBatch(inputs, ids, times);
            scalar.AddPhysicality(ids[0], entity, (short)PhysicalityType.Content, firstCoord,
                Hilbert128.Encode(firstCoord), trajectory, 3, .375, 7, times[0]);
            scalar.AddPhysicality(ids[1], H(9), (short)PhysicalityType.Projection, secondCoord,
                Hilbert128.Encode(secondCoord), [], 0, null, null, times[1]);
        }
        Assert.Equal(2, batch.PhysicalityCount);
        Assert.Equal(Tuples(scalar, IntentStageTable.Physicalities), Tuples(batch, IntentStageTable.Physicalities));
        Assert.Equal(scalar.EmitCopyBinary(IntentStageTable.Physicalities), batch.EmitCopyBinary(IntentStageTable.Physicalities));
        Assert.InRange(batch.AllocatedBytes, batch.TotalTupleBytes, Grant);
    }

    [Fact]
    public void SourceCapture_FitsPhysicalitiesWithoutReservingUnusedNativeTables()
    {
        const int count = 512;
        const long stageGrant = 200_000;
        const long scratchGrant = count * 136L;
        PhysicalityRow[] rows = CaptureRows(count);
        // This is the former production request, against the same real native
        // allocator and grant. Its three unrelated table reservations cannot fit.
        Assert.Throws<OutOfMemoryException>(() => IntentStage.NewBounded(count, stageGrant));
        using var captured = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
            rows, stageGrant, scratchGrant);
        using var expected = IntentStage.New(0);
        foreach (var row in rows)
            expected.AddPhysicality(row.Id, row.EntityId, (short)row.Type,
                [row.CoordX, row.CoordY, row.CoordZ, row.CoordM], row.HilbertIndex,
                row.TrajectoryXyzm ?? [], row.NConstituents,
                row.AlignmentResidual, row.SourceDim, row.ObservedAtUnixUs);
        Assert.Equal(count, captured.PhysicalityCount);
        Assert.Equal(0, captured.EntityCount);
        Assert.Equal(0, captured.AttestationCount);
        Assert.InRange(captured.AllocatedBytes, captured.TotalTupleBytes, stageGrant);
        Assert.Equal(Tuples(expected, IntentStageTable.Physicalities),
            Tuples(captured, IntentStageTable.Physicalities));
    }

    [Theory]
    [InlineData(1, 69_632, "OutOfMemoryException")]
    [InlineData(1_024, 69_632, "InvalidOperationException")]
    [InlineData(200_000, 135, "InvalidOperationException")]
    public void SourceCapture_PreservesNativeAndScratchGrants(
        long stageGrant, long scratchGrant, string exceptionType)
    {
        PhysicalityRow[] rows = CaptureRows(512);
        Exception? error = Record.Exception(() =>
        {
            using var stage = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
                rows, stageGrant, scratchGrant);
        });
        Assert.NotNull(error);
        Assert.Equal(exceptionType, error.GetType().Name);
        Assert.Contains("observations=512", error.Message);
        Assert.Contains($"stageGrantBytes={stageGrant}", error.Message);
        Assert.Contains($"scratchGrantBytes={scratchGrant}", error.Message);
        Assert.NotNull(error.InnerException);
        // A failed capture owns and discards its partial stage. A subsequent
        // ordinary capture still emits every original row under the finite grant.
        using var retry = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
            rows, 200_000, 69_632);
        Assert.Equal(rows.Length, retry.PhysicalityCount);
    }

    [Fact]
    public void SourceCapture_StillRejectsDeclaredIdentityMismatch()
    {
        PhysicalityRow[] rows = CaptureRows(2);
        rows[1] = rows[1] with { Id = H(999) };
        var error = Assert.Throws<InvalidOperationException>(() =>
            NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, Grant));
        Assert.Contains("native physicality batch staging failed", error.Message);
        Assert.Contains("observations=2", error.Message);
    }

    [Fact]
    public void SourceCapture_BoundedAndBulkMatchEveryScalarNativeTuple()
    {
        PhysicalityRow[] rows = MixedCaptureRows();
        // 200 bytes admits the widest row and forces storage reuse between
        // long/empty/short trajectories and non-null/null scalar slots.
        using var bounded = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 200);
        using var bulk = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, Grant);
        using var scalar = ScalarCapture(rows);
        Assert.Equal(rows.Length, bounded.PhysicalityCount);
        Assert.Equal(0, bounded.EntityCount + bounded.AttestationCount);
        byte[] expected = Tuples(scalar, IntentStageTable.Physicalities);
        Assert.Equal(expected, Tuples(bulk, IntentStageTable.Physicalities));
        Assert.Equal(expected, Tuples(bounded, IntentStageTable.Physicalities));
        Assert.Equal(scalar.EmitCopyBinary(IntentStageTable.Physicalities),
            bounded.EmitCopyBinary(IntentStageTable.Physicalities));
        foreach (var row in rows)
            if (row.TrajectoryXyzm is { } trajectory) Array.Fill(trajectory, double.NaN);
        Assert.Equal(expected, Tuples(bounded, IntentStageTable.Physicalities));
    }

    [Fact]
    public void SourceCapture_ReusesScratchWithinTheSameAggregateGrant()
    {
        const long aggregate = 200_000;
        PhysicalityRow[] rows = CaptureRows(512);
        long monolithicScratch = rows.Length * 136L;
        // The actual native old-plus-new reservation cannot grow under the
        // remainder left by the old whole-change transport allocation.
        Assert.Throws<InvalidOperationException>(() =>
            NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
                rows, aggregate - monolithicScratch, monolithicScratch));
        const long boundedScratch = 3 * 136;
        using var captured = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(
            rows, aggregate - boundedScratch, boundedScratch);
        using var scalar = ScalarCapture(rows);
        Assert.Equal(rows.Length, captured.PhysicalityCount);
        Assert.True(captured.AllocatedBytes + boundedScratch <= aggregate);
        Assert.Equal(Tuples(scalar, IntentStageTable.Physicalities),
            Tuples(captured, IntentStageTable.Physicalities));
    }

    [Fact]
    public void SourceCapture_RefusesAnIndivisibleRowOrLaterInvalidIdentity()
    {
        PhysicalityRow[] rows = MixedCaptureRows();
        var tooSmall = Assert.Throws<InvalidOperationException>(() =>
            NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 199));
        Assert.Contains("one physicality transport requires 200 bytes", tooSmall.Message);
        PhysicalityRow saved = rows[17];
        rows[17] = saved with { Id = H(999) };
        using (var partial = IntentStage.NewBounded(0, Grant))
        {
            Assert.Throws<InvalidOperationException>(() =>
                NpgsqlSubstrateWriter.PhysicalityAdmissionBatch.StageManagedObservations(
                    partial, rows, 200, default));
            Assert.Equal(17, partial.PhysicalityCount);
        }
        Assert.Throws<InvalidOperationException>(() =>
            NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 200));
        rows[17] = saved;
        using var retry = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 200);
        using var scalar = ScalarCapture(rows);
        Assert.Equal(Tuples(scalar, IntentStageTable.Physicalities),
            Tuples(retry, IntentStageTable.Physicalities));
    }

    [Fact]
    public void SourceCapture_CancellationAtNativeBoundaryStopsBeforeNextBatch()
    {
        PhysicalityRow[] rows = CaptureRows(30);
        using var stage = IntentStage.NewBounded(0, Grant);
        using var cancelled = new CancellationTokenSource();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                NpgsqlSubstrateWriter.PhysicalityAdmissionBatch.StageManagedObservations(
                    stage, rows, 3 * 136, cancelled.Token);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        bool reachedNativeGate;
        // This existing gate is the worker's only blocking operation. Cancellation
        // while it waits cannot interrupt native work: the first real batch runs,
        // then the production post-call checkpoint must prevent the next one.
        lock (LaplaceCoreGate.Native)
        {
            worker.Start();
            reachedNativeGate = SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10));
            cancelled.Cancel();
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "capture worker did not stop");
        Assert.True(reachedNativeGate, "capture worker did not reach the native gate");
        Assert.IsType<OperationCanceledException>(failure);
        Assert.Equal(3, stage.PhysicalityCount);
        using var prefix = ScalarCapture(rows[..3]);
        Assert.Equal(Tuples(prefix, IntentStageTable.Physicalities),
            Tuples(stage, IntentStageTable.Physicalities));
        Assert.Throws<OperationCanceledException>(() =>
            NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 3 * 136, cancelled.Token));
        using var retry = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage(rows, Grant, 3 * 136);
        Assert.Equal(rows.Length, retry.PhysicalityCount);
    }

    [Fact]
    public void SourceCapture_PreservesAllObservationSourcesUnitsAndReferenceSupplement()
    {
        PhysicalityRow[] rows = CaptureRows(3);
        var first = new SubstrateChange([], [rows[0], rows[1]], [],
            new SubstrateChangeMetadata(H(100), H(50), "capture-one", DateTimeOffset.UnixEpoch, null))
        {
            // Same exact row observed twice remains two occurrences. A different
            // but byte-identical object also remains present.
            PhysicalityObservations = [rows[0], rows[0], rows[0] with { }],
        }.WithSourcePrior(H(50), .75);
        var secondRow = rows[2] with { SourceId = H(51) };
        var second = new SubstrateChange([], [secondRow], [],
            new SubstrateChangeMetadata(H(101), H(51), "capture-two", DateTimeOffset.UnixEpoch, null))
            .WithSourcePrior(H(51), .25);
        using var capture = NpgsqlSubstrateWriter.PhysicalityAdmissionBatch.Capture(
            [first, second], [], default);
        Assert.NotNull(capture);
        Assert.Equal([H(50), H(50), H(50), H(50), H(51)], capture.ObservationSources.ToArray());
        Assert.Equal([H(100), H(100), H(100), H(100), H(101)], capture.ObservationUnits.ToArray());
        Assert.Equal([.75, .75, .75, .75, .25], capture.ObservationPriors.ToArray());
        Assert.Equal(2, capture.RawStages.Count);
        using var expectedFirst = ScalarCapture([rows[0], rows[0], rows[0], rows[1]]);
        Assert.Equal(Tuples(expectedFirst, IntentStageTable.Physicalities),
            Tuples(capture.RawStages[0], IntentStageTable.Physicalities));
        Assert.Equal(1, capture.RawStages[1].PhysicalityCount);
        Assert.InRange(capture.OwnedRawBytes + capture.ObservationPayloadBytes, 1, capture.MaximumBytes);
    }

    [Fact]
    public void SourceCapture_EmptyInputHasNoRowsAndNoObservationBatch()
    {
        using var stage = NpgsqlSubstrateWriter.CaptureManagedPhysicalityStage([], Grant, 136);
        Assert.Equal(0, stage.PhysicalityCount);
        Assert.Equal(0, stage.TotalTupleBytes);
        Assert.Null(NpgsqlSubstrateWriter.PhysicalityAdmissionBatch.Capture([], [], default));
    }

    private static PhysicalityRow[] MixedCaptureRows()
    {
        double[] coordinate = [.1, -0.0, .3, -.4];
        double[] longTrajectory = Trajectory.Build([H(1), H(1), H(2)]);
        double[] shortTrajectory = Trajectory.Build([H(2)]);
        var longRow = new PhysicalityRow(PhysicalityId.Compute(
                Trajectory.ContentIdentity(longTrajectory, out int count), PhysicalityType.Content),
            Trajectory.ContentIdentity(longTrajectory, out _), H(50), PhysicalityType.Content,
            coordinate[0], coordinate[1], coordinate[2], coordinate[3], Hilbert128.Encode(coordinate),
            longTrajectory, count, .375, 7, IntentStage.PgEpochUnixUs - 123);
        var emptyRow = CaptureRows(1)[0];
        var shortRow = longRow with
        {
            Id = PhysicalityId.Compute(Trajectory.ContentIdentity(shortTrajectory, out _), PhysicalityType.Content),
            EntityId = Trajectory.ContentIdentity(shortTrajectory, out _),
            TrajectoryXyzm = shortTrajectory, NConstituents = 1,
            AlignmentResidual = null, SourceDim = null, ObservedAtUnixUs = IntentStage.PgEpochUnixUs + 456,
        };
        return Enumerable.Range(0, 20)
            .SelectMany(_ => new[] { longRow, emptyRow, shortRow, longRow, longRow with { } }).ToArray();
    }

    private static IntentStage ScalarCapture(IEnumerable<PhysicalityRow> rows)
    {
        var stage = IntentStage.New(0);
        foreach (var row in rows)
            stage.AddPhysicality(row.Id, row.EntityId, (short)row.Type,
                [row.CoordX, row.CoordY, row.CoordZ, row.CoordM], row.HilbertIndex,
                row.TrajectoryXyzm ?? [], row.NConstituents,
                row.AlignmentResidual, row.SourceDim, row.ObservedAtUnixUs);
        return stage;
    }

    private static PhysicalityRow[] CaptureRows(int count)
    {
        double[] coordinate = [.1, .2, .3, .4];
        Hilbert128 hilbert = Hilbert128.Encode(coordinate);
        return Enumerable.Range(1, count).Select(i =>
            new PhysicalityRow(PhysicalityId.Compute(H(i), PhysicalityType.Projection),
                H(i), H(50), PhysicalityType.Projection,
                coordinate[0], coordinate[1], coordinate[2], coordinate[3], hilbert,
                null, 0, null, null, IntentStage.PgEpochUnixUs + i)).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public unsafe void PhysicalityBatch_RejectsActualDeclaredPlacementMismatch(int invalidIndex)
    {
        double[] coordinate = [.1, .2, .3, .4];
        PhysicalityDescriptorInputNative[] inputs =
        [
            Input(H(10), PhysicalityType.Projection, coordinate, null, 0, 0, null, null),
            Input(H(11), PhysicalityType.Projection, coordinate, null, 0, 0, null, null),
        ];
        Hash128[] ids = [PhysicalityId.Compute(H(10), PhysicalityType.Projection),
            PhysicalityId.Compute(H(11), PhysicalityType.Projection)];
        ids[invalidIndex] = H(999);
        using var stage = IntentStage.NewBounded(0, Grant);
        Assert.Throws<InvalidOperationException>(() => stage.AddPhysicalityBatch(inputs, ids, [10, 20]));
        // Shared staging admits preceding rows; its caller must discard this
        // failed stage. This explicitly avoids inventing batch rollback.
        Assert.Equal(invalidIndex, stage.PhysicalityCount);
    }

    [Fact]
    public unsafe void PhysicalityBatch_RejectsMisalignedIdsAndTimestampsBeforeStaging()
    {
        PhysicalityDescriptorInputNative[] inputs =
            [Input(H(10), PhysicalityType.Projection, [.1, .2, .3, .4], null, 0, 0, null, null)];
        using var stage = IntentStage.NewBounded(0, Grant);
        Assert.Throws<ArgumentException>(() => stage.AddPhysicalityBatch(inputs, [], [10]));
        Assert.Throws<ArgumentException>(() => stage.AddPhysicalityBatch(inputs,
            [PhysicalityId.Compute(H(10), PhysicalityType.Projection)], []));
        Assert.Equal(0, stage.PhysicalityCount);
    }

    [Fact]
    public void TupleImport_RoundTripsAllThreeNativeTablesAndOwnsItsCopies()
    {
        byte[][] tuples;
        byte[][] wire;
        using (var original = CompleteStage())
        {
            tuples = Tables.Select(t => Tuples(original, t)).ToArray();
            wire = Tables.Select(original.EmitCopyBinary).ToArray();
        }
        using var imported = IntentStage.FromTupleBytes(tuples[0], tuples[1], tuples[2], Grant);
        Assert.Equal(2, imported.EntityCount);
        Assert.Equal(1, imported.PhysicalityCount);
        Assert.Equal(4, imported.AttestationCount);
        Assert.Equal(tuples.Sum(t => (long)t.Length), imported.TotalTupleBytes);
        Assert.InRange(imported.AllocatedBytes, imported.TotalTupleBytes, Grant);
        foreach (byte[] source in tuples) Array.Fill(source, (byte)0xDD);
        for (int i = 0; i < Tables.Length; ++i)
            Assert.Equal(wire[i], imported.EmitCopyBinary(Tables[i]));
    }

    [Theory]
    [InlineData(IntentStageTable.Entities, 0)]
    [InlineData(IntentStageTable.Entities, 1)]
    [InlineData(IntentStageTable.Entities, 2)]
    [InlineData(IntentStageTable.Entities, 3)]
    [InlineData(IntentStageTable.Physicalities, 0)]
    [InlineData(IntentStageTable.Physicalities, 1)]
    [InlineData(IntentStageTable.Physicalities, 2)]
    [InlineData(IntentStageTable.Physicalities, 3)]
    [InlineData(IntentStageTable.Attestations, 0)]
    [InlineData(IntentStageTable.Attestations, 1)]
    [InlineData(IntentStageTable.Attestations, 2)]
    [InlineData(IntentStageTable.Attestations, 3)]
    public void TupleImport_RejectsMalformedNativeFraming(IntentStageTable table, int defect)
    {
        using var original = CompleteStage();
        byte[][] parts = Tables.Select(t => Tuples(original, t)).ToArray();
        int index = (int)table - 1;
        switch (defect)
        {
            case 0: parts[index] = parts[index][..^1]; break;
            case 1: BinaryPrimitives.WriteInt16BigEndian(parts[index], 99); break;
            case 2: BinaryPrimitives.WriteInt32BigEndian(parts[index].AsSpan(2), -2); break;
            case 3: BinaryPrimitives.WriteInt32BigEndian(parts[index].AsSpan(2), int.MaxValue); break;
        }
        Assert.Throws<InvalidOperationException>(() =>
            IntentStage.FromTupleBytes(parts[0], parts[1], parts[2], Grant));
    }

    [Theory]
    [InlineData(IntentStageTable.Entities)]
    [InlineData(IntentStageTable.Physicalities)]
    [InlineData(IntentStageTable.Attestations)]
    public void TupleImport_RejectsWholeCopyStreamsAndWrongTableRouting(IntentStageTable table)
    {
        using var original = CompleteStage();
        byte[][] parts = [[], [], []];
        int index = (int)table - 1;
        parts[index] = original.EmitCopyBinary(table);
        Assert.Throws<InvalidOperationException>(() =>
            IntentStage.FromTupleBytes(parts[0], parts[1], parts[2], Grant));
        parts[index] = Tuples(original, Tables[(index + 1) % Tables.Length]);
        Assert.Throws<InvalidOperationException>(() =>
            IntentStage.FromTupleBytes(parts[0], parts[1], parts[2], Grant));
    }

    [Fact]
    public void TupleImport_EmptyTablesRemainEmptyAndInsufficientNativeGrantRejects()
    {
        using var empty = IntentStage.FromTupleBytes([], [], [], Grant);
        Assert.Equal(0, empty.EntityCount + empty.PhysicalityCount + empty.AttestationCount);
        Assert.Equal(0, empty.TotalTupleBytes);
        using var original = CompleteStage();
        byte[][] parts = Tables.Select(t => Tuples(original, t)).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            IntentStage.FromTupleBytes(parts[0], parts[1], parts[2], 1));
    }

    [Fact]
    public void FullAttestationDecode_PreservesNullableZeroIdsAndAllFieldsThroughOrdinaryRestaging()
    {
        AttestationRow[] originals = Attestations();
        using var first = NativeAttestations(originals[..2]);
        using var second = NativeAttestations(originals[2..]);
        var blobs = new[] { first.TupleBuffer(IntentStageTable.Attestations), second.TupleBuffer(IntentStageTable.Attestations) };
        var decoded = new List<AttestationRow>();
        var parsed = CopyTupleParser.ParseAttestations(blobs, decoded);
        var decodeOnly = new List<AttestationRow>();
        CopyTupleParser.DecodeAttestations(blobs, decodeOnly);

        Assert.Equal(originals.Length, parsed.Rows.Count);
        Assert.Equal(decoded, decodeOnly);
        for (int i = 0; i < originals.Length; ++i)
        {
            AttestationRow expected = originals[i] with { ScoreFp1e9 = 0, SumScoreFp1e9 = Total(originals[i]) };
            Assert.Equal(expected, decoded[i]);
            Assert.Equal(originals[i].LastObservedAtUnixUs - IntentStage.PgEpochUnixUs, parsed.TimestampsPgUs[i]);
            Assert.Equal(originals[i].ObservationCount, parsed.Counts[i]);
            Assert.Equal(Total(originals[i]), parsed.SumScores[i]);
            Assert.Equal(originals[i].FoldReplayable, parsed.FoldReplayable[i]);
        }
        Assert.Null(decoded[0].ObjectId);
        Assert.Null(decoded[0].ContextId);
        Assert.Equal((Hash128?)Hash128.Zero, decoded[1].ObjectId);
        Assert.Equal((Hash128?)Hash128.Zero, decoded[1].ContextId);
        Assert.False(decoded[1].FoldReplayable);
        Assert.NotEqual(0, decoded[2].SumScoreFp1e9);

        using var restaged = IntentStage.NewBounded(0, Grant);
        var mapped = decoded.Select(NpgsqlSubstrateWriter.StageAttestation).ToArray();
        for (int i = 0; i < mapped.Length; ++i)
        {
            Assert.Equal((byte)1, mapped[i].IsAggregated);
            Assert.Equal(Total(originals[i]), mapped[i].SumScoreFp1e9);
            Assert.Equal(originals[i].OpponentRatingFp1e9, mapped[i].OpponentRatingFp1e9);
        }
        restaged.AddAttestationsStaged(mapped, mapped.Length, Masks(decoded));
        Assert.Equal(Tuples(first, IntentStageTable.Attestations)
            .Concat(Tuples(second, IntentStageTable.Attestations)).ToArray(),
            Tuples(restaged, IntentStageTable.Attestations));
        using var imported = IntentStage.FromTupleBytes([], [], Tuples(restaged, IntentStageTable.Attestations), Grant);
        var afterImport = new List<AttestationRow>();
        CopyTupleParser.DecodeAttestations([imported.TupleBuffer(IntentStageTable.Attestations)], afterImport);
        Assert.Equal(decoded, afterImport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullAttestationDecode_RejectsInvalidOutcomeAndTimestampOverflow(bool timestampOverflow)
    {
        using var stage = NativeAttestations(Attestations()[..1]);
        byte[] tuples = Tuples(stage, IntentStageTable.Attestations);
        int offset = FieldPayload(tuples, timestampOverflow ? 7 : 6);
        if (timestampOverflow) BinaryPrimitives.WriteInt64BigEndian(tuples.AsSpan(offset), long.MaxValue);
        else BinaryPrimitives.WriteInt16BigEndian(tuples.AsSpan(offset), 3);
        using var imported = IntentStage.FromTupleBytes([], [], tuples, Grant);
        var blobs = new[] { imported.TupleBuffer(IntentStageTable.Attestations) };
        if (timestampOverflow)
        {
            Assert.Throws<OverflowException>(() => CopyTupleParser.ParseAttestations(blobs, []));
            Assert.Throws<OverflowException>(() => CopyTupleParser.DecodeAttestations(blobs, []));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => CopyTupleParser.ParseAttestations(blobs, []));
            Assert.Throws<InvalidOperationException>(() => CopyTupleParser.DecodeAttestations(blobs, []));
        }
    }

    private static readonly IntentStageTable[] Tables =
        [IntentStageTable.Entities, IntentStageTable.Physicalities, IntentStageTable.Attestations];

    private static unsafe PhysicalityDescriptorInputNative Input(Hash128 entity, PhysicalityType type,
        double[] coordinate, double* trajectory, nuint vertices, int count, double? residual, int? dimension)
    {
        var result = new PhysicalityDescriptorInputNative
        {
            EntityId = entity, Type = (short)type, HilbertIndex = Hilbert128.Encode(coordinate),
            Trajectory = trajectory, TrajectoryVertices = vertices, Constituents = count,
            AlignmentResidualIsNull = residual.HasValue ? 0 : 1, AlignmentResidual = residual ?? 0,
            SourceDimIsNull = dimension.HasValue ? 0 : 1, SourceDim = dimension ?? 0,
        };
        for (int axis = 0; axis < 4; ++axis) result.Coordinate[axis] = coordinate[axis];
        return result;
    }

    private static unsafe byte[] Tuples(IntentStage stage, IntentStageTable table)
    {
        var (pointer, length) = stage.TupleBuffer(table);
        byte[] result = new ReadOnlySpan<byte>((void*)pointer, checked((int)length)).ToArray();
        GC.KeepAlive(stage);
        return result;
    }

    private static IntentStage CompleteStage()
    {
        var stage = NativeAttestations(Attestations());
        stage.AddEntity(H(1), 1, H(90), null);
        stage.AddEntity(H(2), 2, H(90), Hash128.Zero);
        double[] coordinate = [.1, .2, .3, .4];
        stage.AddPhysicality(PhysicalityId.Compute(H(1), PhysicalityType.Projection), H(1),
            (short)PhysicalityType.Projection, coordinate, Hilbert128.Encode(coordinate),
            Trajectory.Build([H(2), H(2), H(3)]), 3, .25, 11, IntentStage.PgEpochUnixUs - 456);
        return stage;
    }

    private static AttestationRow[] Attestations() =>
    [
        new(H(100), H(1), H(90), null, H(50), null, AttestationOutcome.Draw,
            IntentStage.PgEpochUnixUs - 123_456_789, 0, 999, 7_500_000_000, 2_222_222_222_222, 0, default, true),
        new(H(101), H(1), H(90), Hash128.Zero, H(51), Hash128.Zero, AttestationOutcome.Confirm,
            IntentStage.PgEpochUnixUs, 3, 123, 57_777_777_777, 777_123_456_789, 1_000_000_007,
            new Mask256(0x0123_4567_89AB_CDEF, 0xF0E1_D2C3_B4A5_9687, 1UL << 63, 0x8000_0000_0000_0001), false),
        new(H(102), H(2), H(91), H(3), H(52), null, AttestationOutcome.Refute,
            1_700_123_456_789_123, 7, 375_000_001, 110_000_000_003, 2_837_000_000_000, null,
            new Mask256(7, 1UL << 17, 0xFEDC_BA98_7654_3210, 23), true),
        new(H(103), H(2), H(91), null, H(53), H(4), AttestationOutcome.Confirm,
            1_800_987_654_321_999, 11, 888, 89_000_000_017, 1_234_567_890_123, 10_123_456_789,
            new Mask256(1UL << 31, 0, 1UL << 32, 1UL << 63), true),
    ];

    private static long Total(AttestationRow row) => row.SumScoreFp1e9 ?? checked(row.ScoreFp1e9 * row.ObservationCount);

    private static IntentStage NativeAttestations(AttestationRow[] rows)
    {
        // Start with the native staged ABI directly, independently of the
        // Npgsql mapper which the decode/re-stage assertion is testing.
        var native = rows.Select(a => new AttestationStagedNative
        {
            Id = a.Id, SubjectId = a.SubjectId, TypeId = a.TypeId, SourceId = a.SourceId,
            ObjectId = a.ObjectId ?? default, ContextId = a.ContextId ?? default,
            ObjectIsNull = (byte)(a.ObjectId.HasValue ? 0 : 1), ContextIsNull = (byte)(a.ContextId.HasValue ? 0 : 1),
            Outcome = (short)a.Outcome, LastObservedAtUnixUs = a.LastObservedAtUnixUs,
            ObservationCount = a.ObservationCount, ScoreFp1e9 = a.ScoreFp1e9,
            OpponentRdFp1e9 = a.OpponentRdFp1e9, OpponentRatingFp1e9 = a.OpponentRatingFp1e9,
            SumScoreFp1e9 = a.SumScoreFp1e9 ?? 0, IsAggregated = (byte)(a.SumScoreFp1e9.HasValue ? 1 : 0),
            FoldReplayable = (byte)(a.FoldReplayable ? 1 : 0),
        }).ToArray();
        var stage = IntentStage.NewBounded(0, Grant);
        stage.AddAttestationsStaged(native, native.Length, Masks(rows));
        return stage;
    }

    private static byte[] Masks(IReadOnlyList<AttestationRow> rows)
    {
        byte[] result = new byte[rows.Count * 32];
        for (int i = 0; i < rows.Count; ++i) rows[i].HighwayMask.ToByteArray().CopyTo(result, i * 32);
        return result;
    }

    private static int FieldPayload(byte[] tuple, int field)
    {
        int offset = 2;
        for (int i = 0; i <= field; ++i)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(tuple.AsSpan(offset));
            offset += 4;
            if (i == field) return offset;
            if (length >= 0) offset = checked(offset + length);
        }
        throw new ArgumentOutOfRangeException(nameof(field));
    }
}
