using System.Buffers;
using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The order of operations for a source whose rows arrive as native intent stages (every
/// source-generation recipe):
/// <list type="number">
/// <item>Native (engine/core source_reduce.cpp): the source's stages are reduced by identity
/// (one entity and physicality row per id; one claim per attestation id with games and
/// scores added and qualifiers OR-ed), each claimed relation's Highway bit is OR-ed into its
/// entities' masks, rows are routed to lanes by the PostgreSQL HASH partition they land in,
/// and every typed cell is folded as one Glicko-2 rating period per witness.</item>
/// <item>PostgreSQL: each lane is set-level writes into partitions no other lane touches.
/// Rows arrive by binary COPY and the primary keys resolve conflicts; masks ride on the
/// entity rows; prior standing is read once per cell of the source; standing is one COPY of
/// novel cells and one keyed update of cells that already had standing. No existence
/// probes, no per-chunk fold, no separate mask pass.</item>
/// <item>C# (this class): holds the reduction, orders the two phases (entities and
/// physicalities, then claims with the standing they fold), commits and retries lanes, and
/// records the file and layer completions the reduction covers. No row becomes a managed
/// object.</item>
/// </list>
/// Claims and the standing they fold commit in one transaction per lane, so a claim is folded
/// exactly once: a claim already stored is a replay and is not folded again. Changes that
/// carry transient fold inputs, testimony walks or a pre-commit verifier flush the reduction
/// and take the working-set writer unchanged.
/// </summary>
public sealed class SourceReducedApplyWriter : ISubstrateWriter, IConsensusFoldMetrics, IAsyncDisposable
{
    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly PostgresWriteDurability _durability;
    private readonly int _lanes;
    private readonly long _budgetBytes;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SubstrateChange> _held = [];
    private NativeSourceReducer? _reducer;
    private long _observations;
    private long _cells;
    private int _epochRoute = -1;
    private (int Entity, int Physicality, int Claim)? _moduli;

    public SourceReducedApplyWriter(ISubstrateWriter inner, NpgsqlDataSource dataSource,
        PostgresWriteDurability durability, int lanes, long budgetBytes, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lanes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _durability = durability;
        _lanes = lanes;
        _budgetBytes = budgetBytes;
        _log = logger ?? NullLogger.Instance;
    }

    public long ObservationsAccumulated => Interlocked.Read(ref _observations)
        + ((_inner as IConsensusFoldMetrics)?.ObservationsAccumulated ?? 0);

    public long CellsFolded => Interlocked.Read(ref _cells)
        + ((_inner as IConsensusFoldMetrics)?.CellsFolded ?? 0);

    // Writes outside a working set (source vocabulary and artifact provenance at
    // initialization) keep their own writer: they precede the source's stream.
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => _inner.ApplyAsync(change, ct);

    public Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => _inner.ApplyManyAsync(changes, ct);

    public Task<ApplyResult> AppendAsync(IReadOnlyList<SubstrateChange> changes, Hash128 sourceId,
        CancellationToken ct = default) => _inner.AppendAsync(changes, sourceId, ct);

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync([change], ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (changes.Any(static change => !change.EphemeralFoldInputs.IsDefaultOrEmpty
                                             || !change.TestimonyWalks.IsDefaultOrEmpty))
            {
                await FlushAsync(ct).ConfigureAwait(false);
                return await _inner.ApplyWorkingSetAsync(changes, ct).ConfigureAwait(false);
            }
            var sw = Stopwatch.StartNew();
            long entities = 0, physicalities = 0, attestations = 0;
            foreach (SubstrateChange change in changes)
            {
                var (e, p, a) = Absorb(change, ct);
                entities += e;
                physicalities += p;
                attestations += a;
            }
            // Memory, not transport, bounds a reduction: past the budget the reduction
            // commits and the source's remaining stages form the next one.
            ApplyResult? flushed = null;
            if (_reducer is not null && _reducer.Stats.Bytes >= (ulong)_budgetBytes)
                flushed = await FlushAsync(ct).ConfigureAwait(false);
            return new ApplyResult(
                checked((int)entities), flushed?.EntitiesInserted ?? 0,
                checked((int)physicalities), flushed?.PhysicalitiesInserted ?? 0,
                checked((int)attestations), flushed?.AttestationsInserted ?? 0,
                flushed?.RoundTrips ?? 0, sw.Elapsed, TrunkShortcircuitHit: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes,
        Func<CancellationToken, ValueTask> precommitVerifier, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushAsync(ct).ConfigureAwait(false);
            return await _inner.ApplyWorkingSetAsync(changes, precommitVerifier, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DrainFoldsAsync() => _inner.DrainFoldsAsync();

    public Task CompleteFileAsync(string fileLabel, CancellationToken ct = default)
        => _inner.CompleteFileAsync(fileLabel, ct);

    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default) => _inner.FinalizeSourceAsync(sourceId, ct);

    public Task BeginBulkRunAsync(CancellationToken ct = default) => _inner.BeginBulkRunAsync(ct);

    public Task CompleteBulkRunAsync(CancellationToken ct = default) => CompleteBulkRunAsync(null, ct);

    public async Task CompleteBulkRunAsync(Action<BulkRunCompletionPhase>? onPhase, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            onPhase?.Invoke(BulkRunCompletionPhase.ConsensusDrain);
            await FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        await _inner.CompleteBulkRunAsync(onPhase, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _reducer?.Dispose();
        _reducer = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves a change's rows into the native reduction. Completion and canonical-name state
    /// is held until the rows it vouches for are durable.
    /// </summary>
    private (long Entities, long Physicalities, long Attestations) Absorb(SubstrateChange change, CancellationToken ct)
    {
        long entities = 0, physicalities = 0, attestations = 0;
        bool managed = change.Entities.Length > 0 || change.Physicalities.Length > 0 || change.Attestations.Length > 0;
        bool staged = !change.IntentStages.IsDefaultOrEmpty;
        if (managed || staged)
        {
            if (_reducer is null)
            {
                _reducer = NativeSourceReducer.Create();
                // Operational file-metadata edges are stored but never folded or masked.
                _reducer.ExcludeFromFold([FileEntity.MetadataRelationTypeId]);
            }
            if (managed)
            {
                // Provenance builders still emit managed rows; they join the reduction as a stage.
                using IntentStage? stage = NpgsqlSubstrateWriter.BuildManagedStage([change],
                    change.Entities.Length, change.Physicalities.Length, change.Attestations.Length, ct);
                if (stage is not null) _reducer.Add(stage);
                entities += change.Entities.Length;
                physicalities += change.Physicalities.Length;
                attestations += change.Attestations.Length;
            }
            if (staged)
                foreach (IntentStage stage in change.IntentStages)
                {
                    if (stage.IsInvalid || stage.IsClosed) continue;
                    entities += stage.EntityCount;
                    physicalities += stage.PhysicalityCount;
                    attestations += stage.AttestationCount;
                    _reducer.Add(stage);
                    // Ownership transferred with the change, as with the working-set writer.
                    stage.Dispose();
                }
        }
        if (change.HasCompletions || !change.CanonicalNames.IsDefaultOrEmpty)
            _held.Add(change with
            {
                Entities = [],
                Physicalities = [],
                Attestations = [],
                IntentStages = [],
            });
        return (entities, physicalities, attestations);
    }

    private async Task<ApplyResult?> FlushAsync(CancellationToken ct)
    {
        NativeSourceReducer? reducer = _reducer;
        _reducer = null;
        ApplyResult? result = null;
        if (reducer is not null)
        {
            try
            {
                result = await ApplyReductionAsync(reducer, ct).ConfigureAwait(false);
            }
            finally
            {
                reducer.Dispose();
            }
        }
        if (_held.Count > 0)
        {
            // Completion rows commit only after every row they vouch for is durable.
            SubstrateChange[] held = [.. _held];
            _held.Clear();
            await _inner.ApplyWorkingSetAsync(held, ct).ConfigureAwait(false);
        }
        return result;
    }

    private sealed class FlushCounters
    {
        internal long EntitiesInserted, PhysicalitiesInserted, AttestationsInserted, Unplaced;
        internal long CellsMatched, CellsNovel, Masks, RoundTrips;
    }

    private async Task<ApplyResult> ApplyReductionAsync(NativeSourceReducer reducer, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        (int entity, int physicality, int claim) = _moduli ??= await ReadPartitionModuliAsync(ct).ConfigureAwait(false);
        reducer.Partitions(entity, physicality, claim);
        reducer.Route(_lanes);
        SourceReduceStats reduced = reducer.Stats;
        long routeMs = total.ElapsedMilliseconds;
        var counters = new FlushCounters();
        int[] lanes = Enumerable.Range(0, _lanes).ToArray();

        // Entities (with their Highway masks) and physicalities are durable before any
        // claim that names them.
        await Task.WhenAll(lanes.Select(lane => RetryAsync(
            token => ApplyRowsLaneAsync(reducer, lane, counters, token), ct))).ConfigureAwait(false);
        long rowsMs = total.ElapsedMilliseconds;
        PhysicalityClosureLedger.Record(Interlocked.Read(ref counters.EntitiesInserted),
            Interlocked.Read(ref counters.Unplaced));

        // Claims and the standing they fold commit together, one transaction per lane.
        await Task.WhenAll(lanes.Select(lane => RetryAsync(
            token => ApplyEvidenceLaneAsync(reducer, lane, counters, token), ct))).ConfigureAwait(false);
        (Hash128 Entity, Hash128 Type)[] unresolved = reducer.UnresolvedMaskPairs();
        if (unresolved.Length > 0)
            await RetryAsync(token => DepositDynamicMasksAsync(unresolved, counters, token), ct)
                .ConfigureAwait(false);
        long evidenceMs = total.ElapsedMilliseconds;

        SourceReduceStats folded = reducer.Stats;
        Interlocked.Add(ref _observations, (long)folded.Observations);
        Interlocked.Add(ref _cells, (long)folded.Cells);
        _log.LogInformation(
            "SOURCE_REDUCE flush: staged {SE:N0}e/{SP:N0}p/{SA:N0}a -> reduced {E:N0}e/{P:N0}p/{A:N0}a "
            + "({Merged:N0} repeated claim ids merged); stored {EI:N0}e/{PI:N0}p/{AI:N0}a; "
            + "cells {Cells:N0} ({Matched:N0} with prior standing, {Novel:N0} novel) from {Obs:N0} games; "
            + "masks {Masks:N0} stored entity rows gained bits ({Named:N0} claimed entities outside the reduction, "
            + "{Unresolved:N0} dynamic pairs); lanes {Lanes}; round trips {RT:N0}; "
            + "route {RouteMs:N0}ms rows {RowsMs:N0}ms evidence+standing {EvidenceMs:N0}ms total {TotalMs:N0}ms",
            reduced.StagedEntities, reduced.StagedPhysicalities, reduced.StagedAttestations,
            reduced.Entities, reduced.Physicalities, reduced.Attestations, reduced.MergedAttestations,
            counters.EntitiesInserted, counters.PhysicalitiesInserted, counters.AttestationsInserted,
            folded.Cells, counters.CellsMatched, counters.CellsNovel, folded.Observations,
            counters.Masks, reduced.MaskedEntities, unresolved.Length, _lanes, counters.RoundTrips,
            routeMs, rowsMs - routeMs, evidenceMs - rowsMs, total.ElapsedMilliseconds);
        return new ApplyResult(
            checked((int)reduced.Entities), checked((int)counters.EntitiesInserted),
            checked((int)reduced.Physicalities), checked((int)counters.PhysicalitiesInserted),
            checked((int)reduced.Attestations), checked((int)counters.AttestationsInserted),
            checked((int)counters.RoundTrips), total.Elapsed, TrunkShortcircuitHit: false);
    }

    private static string Sql(string name) => SqlCatalog.Get(name).Text;

    private async Task ApplyRowsLaneAsync(NativeSourceReducer reducer, int lane, FlushCounters counters,
        CancellationToken ct)
    {
        var entities = reducer.Stream(SourceReduceStream.Entities, lane);
        var physicalities = reducer.Stream(SourceReduceStream.Physicalities, lane);
        var masks = reducer.Stream(SourceReduceStream.Masks, lane);
        if (entities.Rows == 0 && physicalities.Rows == 0 && masks.Rows == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginAsync(conn, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, tx, Sql("source_reduce.row_tables"), counters, ct).ConfigureAwait(false);
        await CopyInAsync(conn, Sql("source_reduce.entity_copy"), entities, counters, ct).ConfigureAwait(false);
        await CopyInAsync(conn, Sql("source_reduce.physicality_copy"), physicalities, counters, ct).ConfigureAwait(false);
        await CopyInAsync(conn, Sql("source_reduce.mask_copy"), masks, counters, ct).ConfigureAwait(false);
        long inserted = 0, maskRows = 0, unplaced = 0;
        if (entities.Rows > 0)
        {
            // A stored entity keeps its row and gains the claimed relations' bits. Physicality
            // closure is decided over the entities this lane actually admits.
            await using var cmd = new NpgsqlCommand(Sql("source_reduce.entity_admit"), conn, tx) { CommandTimeout = 0 };
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            inserted = reader.GetInt64(0);
            maskRows = reader.GetInt64(1);
            unplaced = reader.GetInt64(2);
            Interlocked.Increment(ref counters.RoundTrips);
        }
        long physicalitiesInserted = physicalities.Rows == 0 ? 0
            : await ExecuteAsync(conn, tx, Sql("source_reduce.physicality_admit"), counters, ct).ConfigureAwait(false);
        if (masks.Rows > 0)
            maskRows += await ExecuteAsync(conn, tx, Sql("source_reduce.mask_update"), counters, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref counters.RoundTrips);
        Interlocked.Add(ref counters.EntitiesInserted, inserted);
        Interlocked.Add(ref counters.Unplaced, unplaced);
        Interlocked.Add(ref counters.PhysicalitiesInserted, physicalitiesInserted);
        Interlocked.Add(ref counters.Masks, maskRows);
    }

    private async Task ApplyEvidenceLaneAsync(NativeSourceReducer reducer, int lane, FlushCounters counters,
        CancellationToken ct)
    {
        var claims = reducer.Stream(SourceReduceStream.Attestations, lane);
        if (claims.Rows == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginAsync(conn, ct).ConfigureAwait(false);
        // The primary key decides novelty. A claim already stored was folded when it was
        // admitted; only the claims admitted now enter this lane's rating periods. A source's
        // first admission stores every claim, so the claims COPY straight into their
        // partitions; a key conflict (a replay or a resumed source) rolls that back and the
        // conflict resolves row by row.
        long admitted;
        await ExecuteAsync(conn, tx, "SAVEPOINT laplace_reduce_claims", counters, ct).ConfigureAwait(false);
        try
        {
            await CopyInAsync(conn, Sql("source_reduce.claim_copy"), claims, counters, ct).ConfigureAwait(false);
            admitted = (long)claims.Rows;
        }
        catch (PostgresException conflict) when (conflict.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await ExecuteAsync(conn, tx, "ROLLBACK TO SAVEPOINT laplace_reduce_claims", counters, ct)
                .ConfigureAwait(false);
            await ExecuteAsync(conn, tx, Sql("source_reduce.claim_tables"), counters, ct).ConfigureAwait(false);
            await CopyInAsync(conn, Sql("source_reduce.claim_stage_copy"), claims, counters, ct).ConfigureAwait(false);
            admitted = await ExecuteAsync(conn, tx, Sql("source_reduce.claim_admit"), counters, ct).ConfigureAwait(false);
        }
        if (admitted == (long)claims.Rows)
            reducer.Admit(lane, default);
        else
            reducer.Admit(lane, await CopyOutAsync(conn, Sql("source_reduce.admitted_copy"), counters, ct)
                .ConfigureAwait(false));
        ulong cells = reducer.BuildCells(lane);
        long matched = 0, novel = 0;
        if (cells > 0)
        {
            // Prior standing: an indexed probe per cell into its owning leaf, read once per
            // cell of this source and row-locked in id order.
            await ExecuteAsync(conn, tx, Sql("source_reduce.cell_tables"), counters, ct).ConfigureAwait(false);
            await CopyInAsync(conn, Sql("source_reduce.cell_copy"),
                reducer.Stream(SourceReduceStream.CellKeys, lane), counters, ct).ConfigureAwait(false);
            reducer.Fold(lane, await CopyOutAsync(conn, Sql("source_reduce.prior_standing"), counters, ct)
                .ConfigureAwait(false));
            var standing = reducer.Stream(SourceReduceStream.Standing, lane);
            if (standing.Rows > 0)
            {
                // Cells with prior standing: the folded state replaces the locked row and this
                // source's games add to its witness count.
                await CopyInAsync(conn, Sql("source_reduce.standing_copy"), standing, counters, ct)
                    .ConfigureAwait(false);
                matched = await ExecuteAsync(conn, tx, Sql("source_reduce.standing_update"), counters, ct)
                    .ConfigureAwait(false);
                if (matched != (long)standing.Rows)
                    throw new InvalidOperationException(
                        $"lane {lane}: {matched} of {standing.Rows} locked consensus cells updated");
            }
            // Novel cells are complete rows: one COPY. A cell another writer created after the
            // prior read violates the primary key; the lane retries from a fresh read.
            var fresh = reducer.Stream(SourceReduceStream.Consensus, lane);
            await CopyInAsync(conn, Sql("source_reduce.consensus_copy"), fresh, counters, ct).ConfigureAwait(false);
            novel = (long)fresh.Rows;
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref counters.RoundTrips);
        Interlocked.Add(ref counters.AttestationsInserted, admitted);
        Interlocked.Add(ref counters.CellsMatched, matched);
        Interlocked.Add(ref counters.CellsNovel, novel);
    }

    // Relation types with no Highway bit are dynamic family members; their bit is the
    // family's, resolved by the one native deposit that owns that lookup.
    private async Task DepositDynamicMasksAsync((Hash128 Entity, Hash128 Type)[] pairs,
        FlushCounters counters, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginAsync(conn, ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(Sql("source_reduce.dynamic_masks"), conn, tx) { CommandTimeout = 0 })
        {
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                pairs.Select(static pair => pair.Entity.ToBytes()).ToArray());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                pairs.Select(static pair => pair.Type.ToBytes()).ToArray());
            Interlocked.Add(ref counters.Masks, (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L));
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref counters.RoundTrips, 2);
    }

    /// <summary>
    /// The HASH partition modulus of each table on the key the reduction routes it by
    /// (entities.id, physicalities.id, attestations.subject_id), or 0 when the table is not
    /// HASH partitioned on exactly that key.
    /// </summary>
    private async Task<(int Entity, int Physicality, int Claim)> ReadPartitionModuliAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(Sql("source_reduce.partition_moduli"), conn);
        var moduli = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                moduli[reader.GetString(0)] = reader.GetInt32(1);
        return (moduli.GetValueOrDefault("entities"), moduli.GetValueOrDefault("physicalities"),
            moduli.GetValueOrDefault("attestations"));
    }

    private async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _epochRoute) < 0)
            {
                await using var probe = new NpgsqlCommand(
                    "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL", conn, tx);
                bool present = (bool)(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);
                Interlocked.CompareExchange(ref _epochRoute, present ? 1 : 0, -1);
            }
            // Every write transaction advances the write epoch before it writes.
            await using var guc = new NpgsqlCommand(
                NpgsqlSubstrateWriter.TransactionGucs(_durability)
                + (Volatile.Read(ref _epochRoute) == 1 ? Sql("write.advance_epoch") : ""),
                conn, tx);
            await guc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql,
        FlushCounters counters, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        long rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref counters.RoundTrips);
        return rows;
    }

    // Moves one native COPY stream to PostgreSQL through a pooled transfer buffer.
    private static async Task CopyInAsync(NpgsqlConnection conn, string copy,
        (IntPtr Bytes, long Length, ulong Rows) stream, FlushCounters counters, CancellationToken ct)
    {
        if (stream.Rows == 0) return;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var target = await conn.BeginRawBinaryCopyAsync(copy, ct).ConfigureAwait(false);
            for (long offset = 0; offset < stream.Length;)
            {
                int n = (int)Math.Min(buffer.Length, stream.Length - offset);
                unsafe
                {
                    new ReadOnlySpan<byte>((byte*)stream.Bytes + offset, n).CopyTo(buffer);
                }
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                offset += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        Interlocked.Increment(ref counters.RoundTrips);
    }

    private static async Task<byte[]> CopyOutAsync(NpgsqlConnection conn, string copy,
        FlushCounters counters, CancellationToken ct)
    {
        await using var source = await conn.BeginRawBinaryCopyAsync(copy, ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes, ct).ConfigureAwait(false);
        Interlocked.Increment(ref counters.RoundTrips);
        return bytes.ToArray();
    }

    // A lane is its own transaction: a deadlock, serialization failure or a key another
    // writer admitted first rolls back only that lane, which then runs again.
    private static async Task RetryAsync(Func<CancellationToken, Task> lane, CancellationToken ct)
    {
        var policy = Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await lane(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt + 1 < policy.MaxAttempts && policy.IsTransient(ex))
            {
                TimeSpan delay = policy.DelayBeforeAttempt(attempt, Random.Shared);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}
