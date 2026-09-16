using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

internal sealed record WorkingSetAcceptedEvidence(
    IReadOnlySet<Hash128> AttestationIds,
    IReadOnlyList<AttestationRow> GeneratedAttestations,
    bool OriginalReplay);

public sealed partial class NpgsqlSubstrateWriter
{
    internal sealed class PhysicalityAdmissionBatch : IDisposable
    {
        internal required IReadOnlyList<IntentStage> OriginalStages;
        internal readonly List<IntentStage> RawStages = [];
        internal readonly List<IntentStage> OwnedRawStages = [];
        internal readonly List<IntentStage> GeneratedStages = [];
        internal readonly List<Hash128> ObservationSources = [];
        internal readonly List<Hash128> ObservationUnits = [];
        internal readonly List<double> ObservationPriors = [];
        internal readonly List<AttestationRow> GeneratedAttestations = [];
        internal readonly long MaximumBytes = IngestSizing.ResolveWorkingSetBudgetBytes();
        internal long OwnedRawBytes;
        internal long ObservationPayloadBytes;
        internal Hash128? OriginalToken;
        internal bool OriginalReceiptPresent;
        internal bool OriginalReplay;
        internal PhysicalityAdmissionReceipt? Receipt;
        internal int GeneratedEntityCount => GeneratedStages.Sum(s => s.EntityCount);
        internal int GeneratedPhysicalityCount => GeneratedStages.Sum(s => s.PhysicalityCount);
        internal int GeneratedAttestationCount => GeneratedStages.Sum(s => s.AttestationCount);

        public void Dispose()
        {
            foreach (var stage in GeneratedStages) stage.Dispose();
            ReleaseRawStages();
        }

        internal void ReleaseRawStages()
        {
            foreach (var stage in OwnedRawStages) stage.Dispose();
            OwnedRawStages.Clear();
            RawStages.Clear();
            OwnedRawBytes = 0;
        }

        internal static PhysicalityAdmissionBatch? Capture(
            IReadOnlyList<SubstrateChange> changes, IReadOnlyList<IntentStage> originalStages,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = new PhysicalityAdmissionBatch { OriginalStages = originalStages };
            long activeScratchBytes = 0;
            try
            {
                long count = 0;
                long largestScratch = 0;
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!change.IntentStages.IsDefaultOrEmpty)
                        foreach (var stage in change.IntentStages)
                            if (!stage.IsInvalid) count = checked(count + stage.PhysicalityCount);
                    var raw = change.PhysicalityObservations;
                    bool supplement = !raw.IsDefaultOrEmpty && !change.Physicalities.IsEmpty;
                    var observations = raw.IsDefaultOrEmpty ? change.Physicalities : raw;
                    // Reserve the possible union before constructing it. The raw
                    // sidecar can add observations but cannot hide selected rows.
                    count = checked(count + observations.Length
                        + (supplement ? change.Physicalities.Length : 0));
                    largestScratch = Math.Max(largestScratch, CaptureScratchBytes(change, ct));
                }
                // Exact payload widths, excluding managed object/allocator bookkeeping.
                // Reserve all source metadata and the largest reused transport before
                // any owned raw native stage is allocated.
                result.ObservationPayloadBytes = checked(count * 40L);
                if (count > Array.MaxLength || result.ObservationPayloadBytes + largestScratch > result.MaximumBytes)
                    throw new InvalidOperationException("physicality source capture exceeds its aggregate allocation grant");
                result.ObservationSources.Capacity = checked((int)count);
                result.ObservationUnits.Capacity = checked((int)count);
                result.ObservationPriors.Capacity = checked((int)count);
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!change.IntentStages.IsDefaultOrEmpty)
                        foreach (var stage in change.IntentStages)
                        {
                            if (stage.IsInvalid || stage.PhysicalityCount == 0) continue;
                            result.RawStages.Add(stage);
                            int covered = 0;
                            foreach (var range in stage.PhysicalitySourceRanges)
                            {
                                if (range.FirstRow != covered || range.RowCount <= 0
                                    || range.RowCount > stage.PhysicalityCount - covered)
                                    throw new InvalidOperationException("native physicality source ranges are incomplete or overlap");
                                double prior = change.RequireSourcePrior(range.SourceId);
                                for (int i = 0; i < range.RowCount; ++i)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    result.AddObservation(range.SourceId, change.Metadata.IntentId, prior);
                                }
                                covered = checked(covered + range.RowCount);
                            }
                            if (covered != stage.PhysicalityCount)
                                throw new InvalidOperationException("native physicality observations lack exact source ownership");
                        }
                    long scratch = CaptureScratchBytes(change, ct);
                    activeScratchBytes = scratch;
                    if (scratch == 0) continue;
                    long remaining = checked(result.MaximumBytes - result.ObservationPayloadBytes
                        - result.OwnedRawBytes - scratch);
                    if (remaining <= 0)
                        throw new InvalidOperationException("physicality source stages exhausted their aggregate allocation grant");
                    var observations = CaptureManagedObservations(change, ct);
                    long transport = ManagedObservationBufferBytes(observations.AsSpan(), ct);
                    var raw = CaptureManagedPhysicalityStage(observations.AsSpan(), remaining, transport, ct);
                    result.OwnedRawStages.Add(raw);
                    result.RawStages.Add(raw);
                    result.OwnedRawBytes = checked(result.OwnedRawBytes + raw.AllocatedBytes);
                    foreach (var physicality in observations)
                    {
                        ct.ThrowIfCancellationRequested();
                        result.AddObservation(physicality.SourceId, change.Metadata.IntentId,
                            change.RequireSourcePrior(physicality.SourceId));
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (result.ObservationSources.Count != 0) return result;
                result.Dispose();
                return null;
            }
            catch (Exception error)
            {
                string context = $"physicality admission capture failed: aggregateGrantBytes={result.MaximumBytes}, "
                    + $"observationMetadataBytes={result.ObservationPayloadBytes}, "
                    + $"ownedRawStageBytes={result.OwnedRawBytes}, scratchBytes={activeScratchBytes}; {error.Message}";
                result.Dispose();
                if (error is OutOfMemoryException)
                    throw new OutOfMemoryException(context, error);
                if (error is InvalidOperationException)
                    throw new InvalidOperationException(context, error);
                throw;
            }
        }

        private void AddObservation(Hash128 source, Hash128 unit, double prior)
        {
            ObservationSources.Add(source);
            ObservationUnits.Add(unit);
            ObservationPriors.Add(prior);
        }

        private static long CaptureScratchBytes(SubstrateChange change, CancellationToken ct)
        {
            var raw = change.PhysicalityObservations;
            if (raw.IsDefaultOrEmpty) return ManagedObservationBufferBytes(change.Physicalities.AsSpan(), ct);
            if (change.Physicalities.IsEmpty) return ManagedObservationBufferBytes(raw.AsSpan(), ct);
            // The transport is reused across bounded native calls. The possible
            // merged reference array and selected-reference set coexist with it;
            // hash buckets/object headers remain allocator bookkeeping.
            long rawBuffer = ManagedObservationBufferBytes(raw.AsSpan(), ct);
            long selectedBuffer = ManagedObservationBufferBytes(change.Physicalities.AsSpan(), ct);
            long transport = Math.Min(checked(rawBuffer + selectedBuffer),
                Math.Max(Math.Max(rawBuffer, selectedBuffer), IngestSizing.ResolveSequentialIoBufferBytes()));
            return checked(transport + (raw.Length + (long)change.Physicalities.Length) * IntPtr.Size
                + change.Physicalities.Length * (long)IntPtr.Size);
        }

        private static ImmutableArray<PhysicalityRow> CaptureManagedObservations(
            SubstrateChange change, CancellationToken ct)
        {
            var raw = change.PhysicalityObservations;
            if (raw.IsDefaultOrEmpty) return change.Physicalities;
            if (change.Physicalities.IsEmpty) return raw;
            // Reference equality only avoids copying the exact same input row
            // twice. It never defines entity/form identity: separate row objects,
            // including byte-identical copies, go to the native canonical owner.
            var missing = new HashSet<PhysicalityRow>(ReferenceEqualityComparer.Instance);
            foreach (var row in change.Physicalities)
            {
                ct.ThrowIfCancellationRequested();
                missing.Add(row);
            }
            foreach (var row in raw)
            {
                ct.ThrowIfCancellationRequested();
                missing.Remove(row);
            }
            if (missing.Count == 0) return raw;
            var complete = ImmutableArray.CreateBuilder<PhysicalityRow>(checked(raw.Length + missing.Count));
            complete.AddRange(raw);
            foreach (var row in change.Physicalities)
            {
                ct.ThrowIfCancellationRequested();
                if (missing.Remove(row)) complete.Add(row);
            }
            return complete.MoveToImmutable();
        }

        private static unsafe long ObservationTransportBytes(PhysicalityRow row)
        {
            int width = row.TrajectoryXyzm?.Length ?? 0;
            if (width % 4 != 0)
                throw new InvalidOperationException("physicality observation contains a partial trajectory vertex");
            return checked(width * (long)sizeof(double)
                + sizeof(PhysicalityDescriptorInputNative) + sizeof(Hash128) + sizeof(long));
        }

        private static long ManagedObservationBufferBytes(
            ReadOnlySpan<PhysicalityRow> observations, CancellationToken ct)
        {
            long total = 0, widest = 0;
            foreach (var row in observations)
            {
                ct.ThrowIfCancellationRequested();
                long bytes = ObservationTransportBytes(row);
                total = checked(total + bytes);
                widest = Math.Max(widest, bytes);
            }
            // Reuse the machine-derived I/O transit window, growing only for an
            // indivisible row. This changes transport grain, never admission count.
            long window = IngestSizing.ResolveSequentialIoBufferBytes();
            return Math.Min(total, Math.Max(widest, window));
        }

        internal static unsafe void StageManagedObservations(
            IntentStage stage, ReadOnlySpan<PhysicalityRow> observations, long maximumBytes,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            long total = 0;
            foreach (var row in observations)
            {
                ct.ThrowIfCancellationRequested();
                long bytes = ObservationTransportBytes(row);
                if (bytes > maximumBytes)
                    throw new InvalidOperationException(
                        $"one physicality transport requires {bytes} bytes; grant is {maximumBytes}");
                total = checked(total + bytes);
            }
            if (observations.IsEmpty) return;
            // All four native payloads are blittable and eight-byte aligned.
            // One pinned allocation is reused; no old per-batch arrays await GC.
            long slots = Math.Min(total, maximumBytes) / sizeof(double);
            if (slots > Array.MaxLength)
                throw new InvalidOperationException("physicality transport buffer exceeds the managed array limit");
            var buffer = new double[checked((int)slots)];
            long bufferBytes = checked(buffer.LongLength * sizeof(double));
            fixed (double* storage = buffer)
            {
                int first = 0;
                while (first < observations.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    long bytes = 0;
                    int count = 0;
                    while (first + count < observations.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        long next = ObservationTransportBytes(observations[first + count]);
                        if (next > bufferBytes - bytes) break;
                        bytes += next;
                        count++;
                    }
                    if (count == 0)
                        throw new InvalidOperationException("one physicality transport does not fit its aligned buffer");
                    var inputs = new Span<PhysicalityDescriptorInputNative>(storage, count);
                    var ids = new Span<Hash128>((byte*)storage + count * (long)sizeof(PhysicalityDescriptorInputNative), count);
                    var times = new Span<long>((byte*)storage + count *
                        (long)(sizeof(PhysicalityDescriptorInputNative) + sizeof(Hash128)), count);
                    double* trajectories = (double*)((byte*)storage + count *
                        (long)(sizeof(PhysicalityDescriptorInputNative) + sizeof(Hash128) + sizeof(long)));
                    int offset = 0;
                    for (int i = 0; i < count; ++i)
                    {
                        ct.ThrowIfCancellationRequested();
                        var row = observations[first + i];
                        ref var input = ref inputs[i];
                        input = default;
                        ids[i] = row.Id;
                        input.EntityId = row.EntityId;
                        input.Type = (short)row.Type;
                        input.Coordinate[0] = row.CoordX; input.Coordinate[1] = row.CoordY;
                        input.Coordinate[2] = row.CoordZ; input.Coordinate[3] = row.CoordM;
                        input.HilbertIndex = row.HilbertIndex;
                        if (row.TrajectoryXyzm is { Length: > 0 } trajectory)
                        {
                            trajectory.AsSpan().CopyTo(new Span<double>(trajectories + offset, trajectory.Length));
                            input.Trajectory = trajectories + offset;
                            input.TrajectoryVertices = (nuint)(trajectory.Length / 4);
                            offset = checked(offset + trajectory.Length);
                        }
                        input.Constituents = row.NConstituents;
                        input.AlignmentResidualIsNull = row.AlignmentResidual is null ? 1 : 0;
                        input.AlignmentResidual = row.AlignmentResidual ?? 0;
                        input.SourceDimIsNull = row.SourceDim is null ? 1 : 0;
                        input.SourceDim = row.SourceDim ?? 0;
                        times[i] = row.ObservedAtUnixUs;
                    }
                    ct.ThrowIfCancellationRequested();
                    stage.AddPhysicalityBatch(inputs, ids, times);
                    ct.ThrowIfCancellationRequested();
                    first = checked(first + count);
                }
            }
        }
    }

    /// <summary>
    /// Capture only physicality tuples under the caller's remaining aggregate
    /// grant. A general row hint reserves all three native table buffers, even
    /// though this source-observation stage never emits entities or attestations.
    /// </summary>
    internal static IntentStage CaptureManagedPhysicalityStage(
        ReadOnlySpan<PhysicalityRow> observations, long stageMaximumBytes, long scratchMaximumBytes,
        CancellationToken ct = default)
    {
        IntentStage? stage = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            stage = IntentStage.NewBounded(0, stageMaximumBytes);
            PhysicalityAdmissionBatch.StageManagedObservations(stage, observations, scratchMaximumBytes, ct);
            return stage;
        }
        catch (Exception error) when (error is OutOfMemoryException or InvalidOperationException)
        {
            long allocatedBytes = stage?.AllocatedBytes ?? 0;
            stage?.Dispose();
            string context = $"physicality source capture failed: observations={observations.Length}, "
                + $"stageGrantBytes={stageMaximumBytes}, scratchGrantBytes={scratchMaximumBytes}, "
                + $"allocatedStageBytes={allocatedBytes}; {error.Message}";
            if (error is OutOfMemoryException)
                throw new OutOfMemoryException(context, error);
            throw new InvalidOperationException(context, error);
        }
        catch
        {
            stage?.Dispose();
            throw;
        }
    }

    private async Task<(int e, int p, int a, long fold, long eSkip, long pSkip, int rt,
        bool journalHit, PostgresCommitReceipt commit, CopyTransactionCounts copy)>
        ApplyStagesCoreAsync(
        IReadOnlyList<IntentStage> stages, PhysicalityAdmissionBatch? physicalityAdmission,
        Hash128? workingSetToken, Hash128? legacyWorkingSetToken, Hash128? legacySingletonToken,
        Hash128? workingSetSource, IReadOnlyList<Hash128> workingSetSources,
        Func<NpgsqlConnection, NpgsqlTransaction, WorkingSetAcceptedEvidence, CancellationToken, Task>? transactionParticipant,
        WorkingSetReconciliation? reconciliation, CancellationToken ct)
    {
        using var connectionDiagnostic = MeasureApplyPhase("connection-and-apply-lock");
        await using var connection = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        bool epochRoute = await SupportsApplyWriteEpochAsync(connection, ct).ConfigureAwait(false);
        await using var transaction = await AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", TransactionGucs(Durability), _log, ct).ConfigureAwait(false);
        connectionDiagnostic?.Complete();
        int preparationRoundTrips = 0;
        if (physicalityAdmission is not null)
        {
            using var admissionDiagnostic = MeasureApplyPhase("physicality-provider-admission");
            // The SQL function performs its complete finite provider read within
            // one active snapshot; the control transaction remains ReadCommitted
            // so later COPY connections' commits are visible to the apply owner.
            await MaterializePhysicalitiesAsync(connection, transaction, physicalityAdmission, ct)
                .ConfigureAwait(false);
            preparationRoundTrips++;
            physicalityAdmission.OriginalToken = workingSetToken;
            if (workingSetToken is { } original)
            {
                await using var probe = connection.CreateCommand();
                probe.Transaction = transaction;
                probe.CommandText = SqlCatalog.Get("ingest.flush_receipt_exists").Text;
                probe.Parameters.AddWithValue(NpgsqlDbType.Bytea, original.ToBytes());
                physicalityAdmission.OriginalReceiptPresent = (bool)(await probe.ExecuteScalarAsync(ct))!;
                physicalityAdmission.OriginalReplay = physicalityAdmission.OriginalReceiptPresent;
                preparationRoundTrips++;
            }
            stages = stages.Concat(physicalityAdmission.GeneratedStages).ToArray();
            // Physicality observations have already been authenticated even when
            // the older source-only receipt exists. A changed form is not hidden
            // behind that receipt, and a backfill cannot replay original callbacks.
            var baseToken = legacyWorkingSetToken ?? IntentStage.SemanticDigestBatch(physicalityAdmission.OriginalStages);
            workingSetToken = ReplayTokenV2(baseToken, stages);
            if (physicalityAdmission.OriginalReplay)
            {
                legacyWorkingSetToken = null;
                legacySingletonToken = null;
                reconciliation = null;
            }
            admissionDiagnostic?.Complete();
        }
        var result = await ApplyPreparedStagesCoreAsync(
            connection, transaction, epochRoute, physicalityAdmission,
            stages, workingSetToken, legacyWorkingSetToken, legacySingletonToken,
            workingSetSource, workingSetSources, transactionParticipant, reconciliation, ct).ConfigureAwait(false);
        result.rt = checked(result.rt + preparationRoundTrips);
        return result;
    }

    private static byte[][] ExportStageTuples(
        IReadOnlyList<IntentStage> stages, IntentStageTable table, long maximumBytes, ref long totalBytes)
    {
        totalBytes = checked(totalBytes + stages.Count * (long)IntPtr.Size);
        if (totalBytes > maximumBytes)
            throw new InvalidOperationException("physicality SQL stage array exceeds its allocation grant");
        var result = new byte[stages.Count][];
        for (int i = 0; i < stages.Count; ++i)
        {
            var tuple = stages[i].TupleBuffer(table);
            totalBytes = checked(totalBytes + tuple.Len);
            if (tuple.Len > Array.MaxLength || totalBytes > maximumBytes)
                throw new InvalidOperationException(
                    $"physicality SQL transport requires {totalBytes} bytes; grant is {maximumBytes}");
            result[i] = new byte[checked((int)tuple.Len)];
            if (tuple.Len != 0) Marshal.Copy(tuple.Ptr, result[i], 0, result[i].Length);
            GC.KeepAlive(stages[i]);
        }
        return result;
    }

    private async Task MaterializePhysicalitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PhysicalityAdmissionBatch input, CancellationToken ct)
    {
        // The additional client payload has one aggregate grant. Borrowed caller
        // stages and allocator/Npgsql bookkeeping are outside this payload count.
        // Reserve the source parameter copies before exporting any tuple arrays.
        long transportBytes = checked(input.ObservationPayloadBytes
            + input.ObservationSources.Count * (long)(2 * IntPtr.Size));
        long transportGrant = checked(input.MaximumBytes - input.OwnedRawBytes - input.ObservationPayloadBytes);
        if (transportBytes >= transportGrant)
            throw new InvalidOperationException("physicality source parameters exhausted their allocation grant");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlCatalog.Get("ingest.physicality_descriptor_materialize").Text;
        foreach (var stages in new[] { (IReadOnlyList<IntentStage>)input.RawStages, input.OriginalStages })
            foreach (var table in new[] { IntentStageTable.Entities, IntentStageTable.Physicalities, IntentStageTable.Attestations })
                command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    ExportStageTuples(stages, table, transportGrant, ref transportBytes));
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            input.ObservationSources.Select(id => id.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            input.ObservationUnits.Select(id => id.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Double, input.ObservationPriors.ToArray());
        // SQL now owns independent parameter copies; the extra raw native stages
        // can be released before receiving the generated stages.
        input.ReleaseRawStages();
        long receiverGrant = checked(input.MaximumBytes - input.ObservationPayloadBytes - transportBytes);
        if (receiverGrant <= 0)
            throw new InvalidOperationException("physicality SQL output has no remaining allocation grant");
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint,
            (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, receiverGrant);
        // A provider round uses both a metadata set and a body set. Content tiers
        // are byte-valued. Include the two actual SPI plan preparations as well.
        const int maximumOperations = 2 + 2 * (byte.MaxValue + 1);
        // Explicit expanded hashing work grant: one 16-byte canonical ID per
        // occurrence. This is a work limit, not a compressed-carrier byte claim.
        long maximumLogicalWork = Math.Max(1, input.MaximumBytes / 16);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, maximumOperations);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, maximumLogicalWork);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("physicality materializer returned no receipt");
        long reportedTupleBytes = reader.GetInt64(13);
        long reportedPeak = reader.GetInt64(12);
        if (reportedTupleBytes < 0 || reportedPeak <= 0 || reportedPeak > receiverGrant
            || reportedTupleBytes > reportedPeak)
            throw new InvalidOperationException("physicality materializer exceeded its declared allocation grant");
        var entities = reader.GetFieldValue<byte[][]>(0);
        var physicalities = reader.GetFieldValue<byte[][]>(1);
        var attestations = reader.GetFieldValue<byte[][]>(2);
        var descriptors = reader.GetFieldValue<byte[][]>(3);
        var views = reader.GetFieldValue<byte[]?[]>(4);
        var floor = reader.GetFieldValue<byte[]>(5);
        long forms = reader.GetInt64(7);
        byte[] generatedSource = reader.GetFieldValue<byte[]>(17);
        var viewStates = reader.GetFieldValue<short[]>(18);
        var viewMissingFirst = reader.GetFieldValue<long[]>(19);
        var viewMissingCount = reader.GetFieldValue<long[]>(20);
        var viewMissingIds = reader.GetFieldValue<byte[][]>(21);
        if (entities.Length != 3 || physicalities.Length != 3 || attestations.Length != 3
            || forms != input.ObservationSources.Count || descriptors.LongLength != forms
            || views.LongLength != forms || floor.Length != 16 || generatedSource.Length != 16
            || descriptors.Any(id => id is not { Length: 16 }))
            throw new InvalidOperationException("physicality materializer receipt and native source forms do not align");
        input.Receipt = new PhysicalityAdmissionReceipt(
            Hash128.FromBytes(floor), Hash128.FromBytes(generatedSource), reader.GetString(6), forms,
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(10), reader.GetInt32(11),
            reportedPeak, reportedTupleBytes, reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16))
        {
            ClientPayloadGrantBytes = input.MaximumBytes,
            SqlPayloadGrantBytes = receiverGrant,
            LogicalWorkGrant = maximumLogicalWork,
            DatabaseOperationGrant = maximumOperations,
        };
        if (string.IsNullOrWhiteSpace(input.Receipt.SnapshotReceipt)
            || input.Receipt.CurrentContentBodies < 0 || input.Receipt.MissingContentBodies < 0
            || input.Receipt.ProviderRounds < 0 || input.Receipt.DatabaseOperations < 0
            || input.Receipt.DatabaseOperations != 2L + 2L * input.Receipt.ProviderRounds
            || input.Receipt.DatabaseOperations > maximumOperations
            || input.Receipt.FloorIndexAddedBytes < 0 || input.Receipt.FloorIndexAddedBytes > reportedPeak
            || input.Receipt.LogicalWork < 0 || input.Receipt.LogicalWork > maximumLogicalWork
            || input.Receipt.StoredVertices < 0)
            throw new InvalidOperationException("physicality materializer returned invalid work or snapshot accounting");
        long returnedTupleBytes = 0;
        for (int i = 0; i < entities.Length; ++i)
            returnedTupleBytes = checked(returnedTupleBytes + entities[i].LongLength
                + physicalities[i].LongLength + attestations[i].LongLength);
        if (returnedTupleBytes != reportedTupleBytes)
            throw new InvalidOperationException("physicality tuple transport differs from its retained receipt");
        // Raw ID arrays, view-state/slice arrays, table arrays, floor/source IDs
        // and snapshot coexist with retained receipts and imported native stages.
        // Reserve both raw transport and decoded immutable receipt payloads.
        long returnedBytes = checked(returnedTupleBytes + forms * (32L + 2 * IntPtr.Size + 2 + 2 * sizeof(long))
            + viewMissingIds.LongLength * (16L + IntPtr.Size)
            + 9L * IntPtr.Size + 32 + input.Receipt.SnapshotReceipt.Length * sizeof(char));
        var decodedViews = PhysicalityViewReceipts.Decode(descriptors, views, viewStates,
            viewMissingFirst, viewMissingCount, viewMissingIds,
            checked(receiverGrant - returnedBytes), maximumLogicalWork);
        returnedBytes = checked(returnedBytes
            + PhysicalityViewReceipts.RetainedPayloadBytes(forms, viewMissingIds.LongLength));
        input.Receipt = input.Receipt with
        {
            Forms = decodedViews.Forms,
            MissingViewReferences = decodedViews.Missing,
        };
        long generatedBytes = 0;
        for (int i = 0; i < entities.Length; ++i)
        {
            long remaining = checked(receiverGrant - returnedBytes - generatedBytes);
            if (remaining <= 0)
                throw new InvalidOperationException("physicality output exhausted its aggregate allocation grant");
            var stage = IntentStage.FromTupleBytes(entities[i], physicalities[i], attestations[i], remaining);
            input.GeneratedStages.Add(stage);
            generatedBytes = checked(generatedBytes + stage.AllocatedBytes);
        }
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("physicality materializer returned multiple receipts");
        input.Receipt = input.Receipt with
        {
            GeneratedEntityRows = input.GeneratedEntityCount,
            GeneratedPhysicalityRows = input.GeneratedPhysicalityCount,
            GeneratedAttestationRows = input.GeneratedAttestationCount,
        };
        // A fully populated native attestation tuple has 14 length prefixes,
        // six IDs, outcome, five int64 fields, replay flag and mask. Its 229-byte
        // width also bounds the decoded row's field/reference payload; managed
        // object headers remain allocator bookkeeping, not measured process RSS.
        const int decodedRowPayload = 2 + 14 * 4 + 6 * 16 + 2 + 5 * 8 + 1 + 32;
        int generatedAttestations = input.GeneratedAttestationCount;
        if (checked(returnedBytes + generatedBytes + generatedAttestations * (long)decodedRowPayload) > receiverGrant)
            throw new InvalidOperationException("physicality evidence decoding exceeds its aggregate allocation grant");
        input.GeneratedAttestations.Capacity = generatedAttestations;
        CopyTupleParser.DecodeAttestations(
            CollectBlobs(input.GeneratedStages, IntentStageTable.Attestations, 14, "attestations"),
            input.GeneratedAttestations);
        _log.LogInformation("PHYSICALITY_ADMISSION forms={Forms} available_views={AvailableViews} missing_views={MissingViews} missing_reference_entries={MissingReferences} provider_rounds={Rounds} database_operations={Operations} peak_bytes={PeakBytes} tuple_bytes={TupleBytes}",
            forms, input.Receipt.Forms.LongCount(form => form.ViewState == PhysicalityViewState.Available),
            input.Receipt.Forms.LongCount(form => form.ViewState == PhysicalityViewState.MissingReference),
            input.Receipt.MissingViewReferences.Length, input.Receipt.ProviderRounds, input.Receipt.DatabaseOperations,
            input.Receipt.ReservedPeakBytes, input.Receipt.TupleBytes);
    }
}
