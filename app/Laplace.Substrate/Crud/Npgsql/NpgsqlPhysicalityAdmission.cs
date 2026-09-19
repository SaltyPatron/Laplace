using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

internal sealed record WorkingSetAcceptedEvidence(
    IReadOnlySet<Hash128> AttestationIds,
    bool OriginalReplay);

internal readonly record struct PhysicalityObservationRow(
    Hash128 EntityId,
    Hash128 PhysicalityId,
    Hash128 SourceId,
    Hash128 SourceUnitId,
    long ObservedAtUnixUs);

public sealed partial class NpgsqlSubstrateWriter
{
    internal sealed class PhysicalityAdmissionBatch : IDisposable
    {
        internal required IReadOnlyList<IntentStage> OriginalStages;
        internal readonly List<IntentStage> RawStages = [];
        internal readonly List<IntentStage> OwnedRawStages = [];
        internal readonly List<IntentStage> GeneratedStages = [];
        internal readonly List<Hash128> ObservationPhysicalityIds = [];
        internal readonly List<Hash128> ObservationEntities = [];
        internal readonly List<Hash128> ObservationSources = [];
        internal readonly List<Hash128> ObservationUnits = [];
        internal readonly List<long> ObservationTimesUnixUs = [];
        internal readonly List<PhysicalityObservationRow> StructuralObservations = [];
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
            try
            {
                long count = 0;
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
                }
                // physicality/entity/source/unit/time. The physicality is already
                // the native typed realization of canonical Merkle content; no
                // second descriptor tree is constructed from these fields.
                result.ObservationPayloadBytes = checked(count * 72L);
                if (count > Array.MaxLength || result.ObservationPayloadBytes > result.MaximumBytes)
                    throw new InvalidOperationException("physicality source capture exceeds its aggregate allocation grant");
                result.ObservationPhysicalityIds.Capacity = checked((int)count);
                result.ObservationEntities.Capacity = checked((int)count);
                result.ObservationSources.Capacity = checked((int)count);
                result.ObservationUnits.Capacity = checked((int)count);
                result.ObservationTimesUnixUs.Capacity = checked((int)count);
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!change.IntentStages.IsDefaultOrEmpty)
                        foreach (var stage in change.IntentStages)
                        {
                            if (stage.IsInvalid || stage.PhysicalityCount == 0) continue;
                            var physicalityBlob = stage.TupleBuffer(IntentStageTable.Physicalities);
                            var physicalityRows = CopyTupleParser.ParsePhysicalities([physicalityBlob]);
                            if (physicalityRows.EntityIds.Count != stage.PhysicalityCount
                                || physicalityRows.TimestampsPgUs.Count != stage.PhysicalityCount)
                                throw new InvalidOperationException("native physicality source rows lost positional alignment");
                            int covered = 0;
                            foreach (var range in stage.PhysicalitySourceRanges)
                            {
                                change.RequireSourcePrior(range.SourceId);
                                if (range.FirstRow != covered || range.RowCount <= 0
                                    || range.RowCount > stage.PhysicalityCount - covered)
                                    throw new InvalidOperationException("native physicality source ranges are incomplete or overlap");
                                for (int i = 0; i < range.RowCount; ++i)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    int row = checked(covered + i);
                                    result.AddObservation(
                                        physicalityRows.Ids[row],
                                        physicalityRows.EntityIds[row],
                                        range.SourceId,
                                        change.Metadata.IntentId,
                                        checked(physicalityRows.TimestampsPgUs[row] + IntentStage.PgEpochUnixUs));
                                }
                                covered = checked(covered + range.RowCount);
                            }
                            if (covered != stage.PhysicalityCount)
                                throw new InvalidOperationException("native physicality observations lack exact source ownership");
                        }
                    var observations = CaptureManagedObservations(change, ct);
                    foreach (var physicality in observations)
                    {
                        ct.ThrowIfCancellationRequested();
                        change.RequireSourcePrior(physicality.SourceId);
                        ValidateManagedPhysicality(physicality);
                        result.AddObservation(physicality.Id, physicality.EntityId, physicality.SourceId,
                            change.Metadata.IntentId, physicality.ObservedAtUnixUs);
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (result.ObservationSources.Count != 0)
                {
                    // The initial count is intentionally conservative so capacity/grant
                    // checks happen before allocation. Once reference overlap is known,
                    // retain the bytes for the rows that actually cross into PostgreSQL.
                    result.ObservationPayloadBytes = checked(result.ObservationSources.Count * 72L);
                    return result;
                }
                result.Dispose();
                return null;
            }
            catch (Exception error)
            {
                string context = $"physicality admission capture failed: aggregateGrantBytes={result.MaximumBytes}, "
                    + $"observationMetadataBytes={result.ObservationPayloadBytes}, "
                    + $"ownedRawStageBytes={result.OwnedRawBytes}; {error.Message}";
                result.Dispose();
                if (error is OutOfMemoryException)
                    throw new OutOfMemoryException(context, error);
                if (error is InvalidOperationException)
                    throw new InvalidOperationException(context, error);
                throw;
            }
        }

        private static void ValidateManagedPhysicality(PhysicalityRow row)
        {
            if (row.NConstituents < 0)
                throw new InvalidOperationException("physicality constituent count cannot be negative");
            Hash128 expected = PhysicalityId.Compute(row.EntityId, row.Type);
            if (row.Id != expected)
                throw new InvalidOperationException(
                    $"physicality identity mismatch: entity={row.EntityId} type={(short)row.Type} "
                    + $"declared={row.Id} recomputed={expected}");

            var trajectory = row.TrajectoryXyzm;
            if (trajectory is null || trajectory.Length == 0)
            {
                if (row.NConstituents != 0)
                    throw new InvalidOperationException(
                        $"physicality declares {row.NConstituents} constituents without a trajectory");
                return;
            }
            if (trajectory.Length % 4 != 0)
                throw new InvalidOperationException(
                    "physicality observation contains a partial trajectory vertex");

            Hash128 manifest = Trajectory.ContentIdentity(trajectory, out int logicalCount);
            if (logicalCount != row.NConstituents)
                throw new InvalidOperationException(
                    $"physicality trajectory count mismatch: entity={row.EntityId} type={(short)row.Type} "
                    + $"declared={row.NConstituents} decoded={logicalCount}");
            if (row.Type == PhysicalityType.Content && manifest != row.EntityId)
                throw new InvalidOperationException(
                    $"content trajectory identity mismatch: entity={row.EntityId} "
                    + $"recomputed={manifest} constituents={logicalCount}");
        }

        private void AddObservation(
            Hash128 physicality, Hash128 entity, Hash128 source, Hash128 unit, long observedAtUnixUs)
        {
            ObservationPhysicalityIds.Add(physicality);
            ObservationEntities.Add(entity);
            ObservationSources.Add(source);
            ObservationUnits.Add(unit);
            ObservationTimesUnixUs.Add(observedAtUnixUs);
        }

        internal Hash128 StructuralObservationDigest()
        {
            if (StructuralObservations.Count == 0) return default;
            var rows = StructuralObservations.ToArray();
            Array.Sort(rows, static (left, right) =>
            {
                int c = left.EntityId.CompareToBytewise(right.EntityId);
                if (c != 0) return c;
                c = left.PhysicalityId.CompareToBytewise(right.PhysicalityId);
                if (c != 0) return c;
                c = left.SourceId.CompareToBytewise(right.SourceId);
                return c != 0 ? c : left.SourceUnitId.CompareToBytewise(right.SourceUnitId);
            });
            ReadOnlySpan<byte> domain = "LaplacePhysicalityObservation/v1\0"u8;
            var bytes = new byte[checked(domain.Length + rows.Length * 64)];
            domain.CopyTo(bytes);
            int offset = domain.Length;
            foreach (var row in rows)
            {
                row.EntityId.WriteBytes(bytes.AsSpan(offset, 16)); offset += 16;
                row.PhysicalityId.WriteBytes(bytes.AsSpan(offset, 16)); offset += 16;
                row.SourceId.WriteBytes(bytes.AsSpan(offset, 16)); offset += 16;
                row.SourceUnitId.WriteBytes(bytes.AsSpan(offset, 16)); offset += 16;
            }
            return Hash128.Blake3(bytes);
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
        IReadOnlyList<EntityInterpretationRow> managedInterpretations,
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
        var originalToken = workingSetToken;
        bool originalReceiptPresent = false;
        if (physicalityAdmission is not null)
        {
            using var admissionDiagnostic = MeasureApplyPhase("physicality-provider-admission");
            PrepareCanonicalPhysicalityObservations(physicalityAdmission, ct);
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
            // Physicality observations have already been authenticated even when
            // the older source-only receipt exists. A changed form is not hidden
            // behind that receipt, and a backfill cannot replay original callbacks.
            var baseToken = legacyWorkingSetToken ?? IntentStage.SemanticDigestBatch(physicalityAdmission.OriginalStages);
            workingSetToken = ReplayTokenV2(baseToken, stages,
                physicalityAdmission.StructuralObservationDigest());
            if (physicalityAdmission.OriginalReplay)
            {
                legacyWorkingSetToken = null;
                legacySingletonToken = null;
                reconciliation = null;
            }
            originalReceiptPresent = physicalityAdmission.OriginalReceiptPresent;
            admissionDiagnostic?.Complete();
        }
        else if (originalToken is { } original)
        {
            // An older semantic receipt proves the source callback was accepted,
            // but does not prove the new interpretation sidecar was published.
            await using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = SqlCatalog.Get("ingest.flush_receipt_exists").Text;
            probe.Parameters.AddWithValue(NpgsqlDbType.Bytea, original.ToBytes());
            originalReceiptPresent = (bool)(await probe.ExecuteScalarAsync(ct))!;
            preparationRoundTrips++;
            if (originalReceiptPresent)
            {
                legacyWorkingSetToken = null;
                legacySingletonToken = null;
                reconciliation = null;
            }
        }
        var interpretations = CollectEntityInterpretations(stages, managedInterpretations, ct);
        if (workingSetToken is { } semanticToken)
            workingSetToken = InterpretationReplayToken(semanticToken, interpretations);

        var result = await ApplyPreparedStagesCoreAsync(
            connection, transaction, epochRoute, physicalityAdmission,
            stages, interpretations, workingSetToken, originalToken, originalReceiptPresent,
            legacyWorkingSetToken, legacySingletonToken,
            workingSetSource, workingSetSources, transactionParticipant, reconciliation, ct).ConfigureAwait(false);
        result.rt = checked(result.rt + preparationRoundTrips);
        return result;
    }

    internal static IntentStage ImportPhysicalityOutputStage(
        ReadOnlySpan<byte> entities, ReadOnlySpan<byte> physicalities,
        ReadOnlySpan<byte> attestations, ReadOnlySpan<byte> interpretations,
        bool interpretationsComplete, long maximumBytes)
    {
        if (!interpretationsComplete && !interpretations.IsEmpty)
            throw new InvalidOperationException("incomplete physicality interpretation transport contains auxiliary rows");
        var stage = IntentStage.FromTupleBytes(entities, physicalities, attestations, maximumBytes);
        try
        {
            // A complete empty stream is meaningful and must replace the legacy
            // entity-summary fallback just as a nonempty complete stream does.
            if (interpretationsComplete) stage.ImportEntityInterpretations(interpretations);
            return stage;
        }
        catch
        {
            stage.Dispose();
            throw;
        }
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

    private static async Task<int> PersistPhysicalityObservationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PhysicalityAdmissionBatch admission, CancellationToken ct)
    {
        if (admission.StructuralObservations.Count == 0) return 0;
        var rows = admission.StructuralObservations;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlCatalog.Get("ingest.physicality_observations").Text;
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(static r => r.EntityId.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(static r => r.PhysicalityId.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(static r => r.SourceId.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(static r => r.SourceUnitId.ToBytes()).ToArray());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            rows.Select(static r => r.ObservedAtUnixUs).ToArray());
        long writes = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        if (admission.Receipt is { } receipt)
            admission.Receipt = receipt with { PhysicalityObservationWrites = writes };
        return 1;
    }

    private void PrepareCanonicalPhysicalityObservations(
        PhysicalityAdmissionBatch input, CancellationToken ct)
    {
        int count = input.ObservationPhysicalityIds.Count;
        if (input.ObservationEntities.Count != count || input.ObservationSources.Count != count
            || input.ObservationUnits.Count != count || input.ObservationTimesUnixUs.Count != count)
            throw new InvalidOperationException("physicality provenance lost positional alignment");

        input.StructuralObservations.Capacity = count;
        var forms = ImmutableArray.CreateBuilder<PhysicalityFormReceipt>(count);
        for (int i = 0; i < count; ++i)
        {
            ct.ThrowIfCancellationRequested();
            Hash128 physicalityId = input.ObservationPhysicalityIds[i];
            input.StructuralObservations.Add(new PhysicalityObservationRow(
                input.ObservationEntities[i], physicalityId,
                input.ObservationSources[i], input.ObservationUnits[i],
                input.ObservationTimesUnixUs[i]));
            forms.Add(new PhysicalityFormReceipt(
                physicalityId, physicalityId, PhysicalityViewState.Available, 0, 0));
        }

        input.Receipt = new PhysicalityAdmissionReceipt(
            default, default, "canonical-merkle-physicality/v1", count,
            0, 0, 0, 0, input.ObservationPayloadBytes, 0, 0, count, 0)
        {
            ClientPayloadGrantBytes = input.MaximumBytes,
            SqlPayloadGrantBytes = 0,
            LogicalWorkGrant = count,
            DatabaseOperationGrant = 1,
            GeneratedEntityRows = 0,
            GeneratedPhysicalityRows = 0,
            PhysicalityObservationRows = count,
            Forms = forms.MoveToImmutable(),
            MissingViewReferences = [],
        };
        _log.LogInformation(
            "PHYSICALITY_RECORDING forms={Forms} generated_entities=0 generated_physicalities=0 observation_bytes={Bytes}",
            count, input.ObservationPayloadBytes);
    }
}
