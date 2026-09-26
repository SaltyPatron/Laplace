using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>Novel attestations accepted by one working-set transaction. Rows is that exact
/// set as persisted (duplicates collapsed to their summed observation), whether the
/// testimony was staged by managed code or natively, so the consensus participant folds
/// the load's own delta in the same transaction.</summary>
internal sealed record WorkingSetAcceptedEvidence(IReadOnlySet<Hash128> AttestationIds)
{
    public IReadOnlyList<AttestationRow> Rows { get; init; } = [];
}

public sealed partial class NpgsqlSubstrateWriter
{
    /// <summary>
    /// A managed physicality row is the typed realization of its entity: its id is
    /// PhysicalityId(entity, type) and a Content trajectory's packed child ids Merkle
    /// back to the entity id. Reject a row whose declared identity disagrees before
    /// it reaches the native stage.
    /// </summary>
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
            throw new InvalidOperationException("physicality contains a partial trajectory vertex");

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

    private static unsafe long PhysicalityTransportBytes(PhysicalityRow row)
    {
        int width = row.TrajectoryXyzm?.Length ?? 0;
        if (width % 4 != 0)
            throw new InvalidOperationException("physicality contains a partial trajectory vertex");
        return checked(width * (long)sizeof(double)
            + sizeof(PhysicalityDescriptorInputNative) + sizeof(Hash128) + sizeof(long));
    }

    /// <summary>
    /// Stage managed physicality rows into the native stage in bounded batches. One
    /// pinned buffer carries the native inputs, declared ids, timestamps and packed
    /// trajectories; batching changes transport grain only, never the staged set.
    /// </summary>
    internal static unsafe void StageManagedPhysicalities(
        IntentStage stage, ReadOnlySpan<PhysicalityRow> rows, long maximumBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        long total = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            ValidateManagedPhysicality(row);
            long bytes = PhysicalityTransportBytes(row);
            if (bytes > maximumBytes)
                throw new InvalidOperationException(
                    $"one physicality transport requires {bytes} bytes; grant is {maximumBytes}");
            total = checked(total + bytes);
        }
        if (rows.IsEmpty) return;
        // All four native payloads are blittable and eight-byte aligned.
        long slots = Math.Min(total, maximumBytes) / sizeof(double);
        if (slots > Array.MaxLength)
            throw new InvalidOperationException("physicality transport buffer exceeds the managed array limit");
        var buffer = new double[checked((int)slots)];
        long bufferBytes = checked(buffer.LongLength * sizeof(double));
        fixed (double* storage = buffer)
        {
            int first = 0;
            while (first < rows.Length)
            {
                ct.ThrowIfCancellationRequested();
                long bytes = 0;
                int count = 0;
                while (first + count < rows.Length)
                {
                    long next = PhysicalityTransportBytes(rows[first + count]);
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
                    var row = rows[first + i];
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
                stage.AddPhysicalityBatch(inputs, ids, times);
                first = checked(first + count);
            }
        }
    }

    private async Task<(int e, int p, int a, long fold, long eSkip, long pSkip, int rt,
        bool journalHit, PostgresCommitReceipt commit, CopyTransactionCounts copy)>
        ApplyStagesCoreAsync(
        IReadOnlyList<IntentStage> stages,
        Hash128? workingSetToken,
        Hash128? workingSetSource, IReadOnlyList<Hash128> workingSetSources,
        Func<NpgsqlConnection, NpgsqlTransaction, WorkingSetAcceptedEvidence, CancellationToken, Task>? transactionParticipant,
        IngestCompletionRows completions, CancellationToken ct)
    {
        using var connectionDiagnostic = MeasureApplyPhase("connection-and-apply-lock");
        await using var connection = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        bool epochRoute = await SupportsApplyWriteEpochAsync(connection, ct).ConfigureAwait(false);
        if (_leafModulus == 0)
            _leafModulus = await StagedSourceWriter.ModulusAsync(connection, ct).ConfigureAwait(false);
        await using var transaction = await AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", TransactionGucs(Durability), _log, ct).ConfigureAwait(false);
        connectionDiagnostic?.Complete();

        return await ApplyPreparedStagesCoreAsync(
            connection, transaction, epochRoute, stages, workingSetToken,
            workingSetSource, workingSetSources, transactionParticipant,
            completions, ct).ConfigureAwait(false);
    }
}
