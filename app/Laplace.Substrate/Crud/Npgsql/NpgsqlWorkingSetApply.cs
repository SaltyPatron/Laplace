using System.Runtime.InteropServices;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Persist-in-bulk step of the shared ingest recipe (the write protocol of
/// docs/specs/06_Engineering_Ruleset.txt Rule #8). Convergence during composition
/// already decided which rows are novel, so the server does two things: (1) one
/// bulk in-transaction verification of the claimed-novel ids, which catches rows a
/// concurrent writer committed between the unlocked descent and this transaction,
/// and (2) plain COPY of what survives, in entities → physicalities → attestations
/// order. No temp-table anti-join and no ON CONFLICT on the COPY path.
///
/// The verification probe covers the whole claimed-novel set, not only its
/// frontier: another writer commits compositions rooted at its own roots, which can
/// sit strictly below this set's novel frontier (a novel parent here may contain a
/// child committed standalone elsewhere; probing only the parent would miss the
/// child and COPY would hit a primary-key violation).
///
/// Entity presence is read with entities_stored_bitmap, not the perfcache: this
/// probe decides what is written, and tier-0 rows are themselves persisted through
/// this path. The one shortcut is conditioned on database state: when the target
/// already holds the Unicode source's layer-0 completion receipt (checked once per
/// bulk run), tier-0 entity ids are answered present client-side without a probe.
/// Before that receipt exists, every tier-0 row goes through probe and COPY.
///
/// Attestation presence is always verified: a claim that embeds a novel entity can
/// already exist, so structural novelty never licenses skipping its probe.
/// </summary>
public sealed partial class NpgsqlSubstrateWriter
{
    internal static readonly int ApplyParallelism = CpuTopology.ResolveApplyPartitions();
    private static readonly IngestSizing.ApplyIoPlan ApplySizing =
        IngestSizing.ResolveApplyIo(ApplyParallelism);

    /// <summary>
    /// Process-wide bound on connections held by COPY groups at once.
    ///
    /// <see cref="PostgresResourcePlan"/> sizes the pool as
    /// <c>1 control + 2p (COPY fan + fold fan) + observability</c>, budgeting the COPY fan
    /// at p. The physicality and attestation phases overlap and each fans out to
    /// <see cref="ApplyParallelism"/> groups, so without this bound live COPY connections
    /// could reach 2p and take the fold fan's and observability's share of the pool.
    ///
    /// Group count is not changed (id-range disjointness and payload sizing decide it);
    /// only the number holding a connection at once is bounded, so extra groups wait for
    /// a slot. Derived from the same plan that sizes MaxPoolSize.
    /// </summary>
    private static readonly SemaphoreSlim CopyConnectionBudget =
        new(ResolveCopyConnectionBudget(), ResolveCopyConnectionBudget());

    internal static int ResolveCopyConnectionBudget()
    {
        var plan = PostgresResourcePlan.Current;
        // Minus the control owner, the observability owners and the fold's reserved
        // headroom: the headroom is not fan capacity, and counting it would give a COPY
        // budget wider than one phase's fan-out.
        int fans = plan.IngestConnectionOwners - 1
                 - plan.ObservabilityConnectionOwners
                 - plan.FoldPoolHeadroomOwners;
        return Math.Max(1, fans / 2);
    }
    /// <summary>
    /// Write-epoch accounting, logged only; no probe or skip decision reads it. Every
    /// write transaction advances laplace.apply_write_epoch before writing; the epoch is
    /// a plain sequence, so advances survive rollback and crash (an aborted write still
    /// counts as a foreign write).
    ///
    /// <see cref="_epochAfterLastCommit"/> is last_value read on the control connection
    /// after the whole apply committed (control transaction, COPY sub-transactions and
    /// merge sub-transactions). At the next apply the control advance returns v, and
    /// v − 1 − baseline − ownBumpsSinceBaseline counts nextval calls by other
    /// transactions in between. Both approximations overcount foreign writes, never
    /// undercount: advances by this process's consensus fold and mask lanes count as
    /// foreign, and an advance landing between the merge commit and the last_value read
    /// is absorbed into the baseline. <see cref="_epochOwnBumpsSinceBaseline"/> covers
    /// the path that advances without re-reading the baseline: a journal-replay apply.
    /// </summary>
    private int _applyWriteEpochRoute = -1;

    /// <summary>The substrate tables' one HASH modulus, read once per writer.</summary>
    private int _leafModulus;
    private long _epochAfterLastCommit = -1;
    private long _epochOwnBumpsSinceBaseline;

    /// <summary>
    /// Once-per-writer to_regclass probe: an older
    /// installed extension has no epoch sequence and every bump site must
    /// degrade to the exact pre-epoch behavior instead of failing the apply.
    /// </summary>
    private async ValueTask<bool> SupportsApplyWriteEpochAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        int known = Volatile.Read(ref _applyWriteEpochRoute);
        if (known >= 0) return known == 1;

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regclass('laplace.apply_write_epoch') IS NOT NULL";
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _applyWriteEpochRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _applyWriteEpochRoute) == 1;
    }

    /// <summary>
    /// Tier-0 completeness gate, resolved ONCE per bulk run: true iff the
    /// UnicodeDecomposer has completed layer 0 in the target DB.
    /// While true, every tier-0 entity id is present by definition (the t0
    /// space is closed and fully seeded — UCD single-origin law) and skips
    /// the presence probe client-side. Conservative by construction: absent
    /// completion (fresh DB, mid-unicode-seed) leaves the gate off and every t0
    /// row probes as before. Entities only — t0 physicalities are NOT
    /// guaranteed 1:1 (projections land after identity content).
    /// </summary>
    private bool _tier0LayerComplete;

    public async Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        ApplySizing.Log();
        _tier0LayerComplete = await QueryTier0LayerCompleteAsync(ct);
        if (_tier0LayerComplete)
            _log.LogInformation(
                "WS_APPLY tier-0 gate ON: unicode L0 layer completion present — "
                + "tier-0 entity ids answer presence client-side, zero probes");
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        _tier0LayerComplete = false;
        // Every ingest's completion drains GIN pending lists after the consensus
        // writer has drained, whatever host drives it. A failed drain fails
        // completion rather than reporting the admitted rows read-ready.
        var drain = System.Diagnostics.Stopwatch.StartNew();
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await NpgsqlIngestOps.CleanGinPendingListsAsync(conn, ct).ConfigureAwait(false);
        _log.LogInformation("WS_APPLY completion gin_drain_ms={GinMs}", drain.ElapsedMilliseconds);
    }

    private async Task<bool> QueryTier0LayerCompleteAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT EXISTS (SELECT 1 FROM laplace.ingest_layer_completion "
            + "WHERE witness_id = laplace.source_id('UnicodeDecomposer') AND layer = 0)";
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// Persists one whole working set in a single serialized transaction, claiming
    /// an idempotency receipt in laplace.ingest_flush_journal keyed by the working
    /// set's token over its intent ids. A retry after an ambiguous commit finds the
    /// receipt and returns a no-op instead of applying the additive attestation
    /// merges twice.
    /// </summary>
    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyWorkingSetAsync((IReadOnlyList<SubstrateChange>)new[] { change }, ct);
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return ApplyManyInternalAsync(
                changes, workingSetKey: null, transactionParticipant: null, ct);
        changes = CanonicalWorkingSetOrder(changes);
        return ApplyManyInternalAsync(
            changes, WorkingSetToken(changes), transactionParticipant: null, ct);
    }

    private static IReadOnlyList<SubstrateChange> CanonicalWorkingSetOrder(
        IReadOnlyList<SubstrateChange> changes)
    {
        if (changes.Count < 2) return changes;
        Hash128 firstSource = changes[0].Metadata.SourceId;
        bool mixed = false;
        for (int i = 1; i < changes.Count; i++)
            if (changes[i].Metadata.SourceId != firstSource) { mixed = true; break; }
        if (!mixed) return changes;

        return changes
            .OrderBy(change => change.Metadata.SourceId, Hash128BytewiseOrder)
            .ThenBy(change => change.Metadata.IntentId, Hash128BytewiseOrder)
            .ToArray();
    }

    private static readonly IComparer<Hash128> Hash128BytewiseOrder =
        Comparer<Hash128>.Create(static (left, right) => left.CompareToBytewise(right));

    private static Hash128 WorkingSetToken(IReadOnlyList<SubstrateChange> changes)
    {
        int ephemeralCount = 0;
        for (int i = 0; i < changes.Count; i++)
            if (!changes[i].EphemeralFoldInputs.IsDefaultOrEmpty)
                ephemeralCount = checked(ephemeralCount + changes[i].EphemeralFoldInputs.Length);

        // Without transient inputs the token is BLAKE3 over the ordered intent ids.
        // Transient inputs additionally bind their calculation receipt ids, so a
        // change cannot replay-hit the receipt of a different calculation. The
        // score is not bound: the receipt identifies the calculation, not its value.
        if (ephemeralCount == 0)
        {
            var ordinary = new byte[changes.Count * 16];
            for (int i = 0; i < changes.Count; i++)
                changes[i].Metadata.IntentId.WriteBytes(ordinary.AsSpan(i * 16, 16));
            return Hash128.Blake3(ordinary);
        }

        var inputs = new List<EphemeralFoldInput>(ephemeralCount);
        var buf = new byte[checked(changes.Count * 16 + 4 + ephemeralCount * 32)];
        for (int i = 0; i < changes.Count; i++)
        {
            changes[i].Metadata.IntentId.WriteBytes(buf.AsSpan(i * 16, 16));
            if (!changes[i].EphemeralFoldInputs.IsDefaultOrEmpty)
                inputs.AddRange(changes[i].EphemeralFoldInputs);
        }
        inputs.Sort(static (x, y) =>
        {
            int c = x.AttestationId.CompareToBytewise(y.AttestationId);
            return c != 0 ? c : x.CalculationReceiptId.CompareToBytewise(y.CalculationReceiptId);
        });
        int offset = changes.Count * 16;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            buf.AsSpan(offset, 4), ephemeralCount);
        offset += 4;
        foreach (var input in inputs)
        {
            input.AttestationId.WriteBytes(buf.AsSpan(offset, 16));
            input.CalculationReceiptId.WriteBytes(buf.AsSpan(offset + 16, 16));
            offset += 32;
        }
        return Hash128.Blake3(buf);
    }

    private static async Task InsertJournalReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Hash128 token,
        Hash128? source,
        IReadOnlyList<Hash128> sources,
        string receiptKind,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "WITH receipt AS ("
            + " INSERT INTO laplace.ingest_flush_journal"
            + " (working_set_id, source_id, receipt_kind) VALUES ($1, $2, $3)"
            + " RETURNING working_set_id)"
            + " INSERT INTO laplace.ingest_flush_journal_sources (working_set_id, source_id)"
            + " SELECT receipt.working_set_id, source_id"
            + " FROM receipt CROSS JOIN unnest($4::bytea[]) AS source_id";
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, token.ToBytes());
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = source is { } sourceId ? sourceId.ToBytes() : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Bytea,
        });
        command.Parameters.AddWithValue(NpgsqlDbType.Text, receiptKind);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = sources.Select(sourceId => sourceId.ToBytes()).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<(int e, int p, int a, long fold, long eSkip, long pSkip, int rt,
        bool journalHit, PostgresCommitReceipt commit, CopyTransactionCounts copy)>
        ApplyPreparedStagesCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, bool epochRoute,
        IReadOnlyList<IntentStage> stages,
        Hash128? workingSetToken,
        Hash128? workingSetSource,
        IReadOnlyList<Hash128> workingSetSources,
        Func<NpgsqlConnection, NpgsqlTransaction, WorkingSetAcceptedEvidence, CancellationToken, Task>? transactionParticipant,
        IngestCompletionRows completions,
        CancellationToken ct)
    {
        using var preparationDiagnostic = MeasureApplyPhase("native-tuples-and-merge-preparation");
        var prepSw = System.Diagnostics.Stopwatch.StartNew();
        var copyTransactions = new CopyTransactionCounts();
        var entBlobs = CollectBlobs(stages, IntentStageTable.Entities, 3, "entities");
        var ents = CopyTupleParser.ParseEntities(entBlobs);
        var physBlobs = CollectBlobs(stages, IntentStageTable.Physicalities, 10, "physicalities");
        // 14 columns, including fold_replayable; must equal ATTESTATION_COL_COUNT in
        // engine/core/src/intent_stage.c. A COPY blob whose field count disagrees
        // with the target table would shift columns silently, so it is rejected here.
        var attBlobs = CollectBlobs(stages, IntentStageTable.Attestations, 14, "attestations");
        long blobMs = prepSw.ElapsedMilliseconds;

        var phys = CopyTupleParser.ParsePhysicalities(physBlobs, decodeBodies: false);
        // The consensus participant folds the accepted evidence set itself, so it needs
        // every staged row decoded, managed and native alike, in staging order.
        List<AttestationRow>? decodedAtts = transactionParticipant is null ? null : new();
        var atts = CopyTupleParser.ParseAttestations(attBlobs, decodedAtts);
        if (transactionParticipant is null && atts.FoldReplayable.Any(static replayable => !replayable))
            throw new InvalidOperationException(
                "non-replayable categorical evidence requires the atomic consensus participant");
        long parseMs = prepSw.ElapsedMilliseconds;

        // Distinct entity ids in first-seen order across every staged intent.
        // Each builder deduplicates only its own stage, and one working set
        // combines many changes, so uniqueness across them is enforced here.
        // COPY has no ON CONFLICT: one duplicate in the claimed-novel set would
        // abort the whole working set.
        //
        // Identity is the content id; tier is not part of it. The first row for
        // an id is kept.
        //
        // Physicalities are verified by their own id, never inferred from their
        // entity: a physicality can arrive for an entity that is already stored.
        // Only first-occurrence row indices are collected here; probe id lists
        // are built later, after cache filtering.
        // Tier-0 gate (snapshot once per apply): while it is on, tier-0 ids
        // never enter the probe and go straight into the present set.
        bool tier0Gate = _tier0LayerComplete;
        var dedupeSw = System.Diagnostics.Stopwatch.StartNew();
        var firstEntIdx = DistinctEntityRowIndices(ents, tier0Gate, out var tier0Present);
        int distinctStagedEntities = firstEntIdx.Count + (tier0Present?.Count ?? 0);
        long entDedupeMs = dedupeSw.ElapsedMilliseconds;
        var physIdSet = new HashSet<Hash128>(phys.Ids.Count);
        var probePhysIds = new List<Hash128>(phys.Ids.Count);
        for (int i = 0; i < phys.Ids.Count; i++)
            if (physIdSet.Add(phys.Ids[i]))
                probePhysIds.Add(phys.Ids[i]);

        // Attestation duplicate collapse, exactly apply_batch's semantics:
        // representative = latest-ts staged row, observation counts sum, and
        // sum_score_fp1e9 sums with them — the persisted evidence stays the
        // exact record of what the group folded.
        // One group per attestation id, in first-seen order; attGroupOfRow maps each
        // staged row to its group.
        int attRowCount = atts.Ids.Count;
        var attGroupOf = new Dictionary<Hash128, int>(attRowCount);
        var attGroupOfRow = new int[attRowCount];
        var attRep = new int[attRowCount];
        var attMaxTs = new long[attRowCount];
        var attGames = new long[attRowCount];
        var attSum = new long[attRowCount];
        int attGroupCount = 0;
        // The keyed attestation probe needs the partition keys parallel to the
        // probed ids, one per group.
        var probeAttIds = new List<Hash128>(attRowCount);
        var probeAttTypes = new List<Hash128>(attRowCount);
        var probeAttSubjects = new List<Hash128>(attRowCount);
        for (int i = 0; i < attRowCount; i++)
        {
            ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(attGroupOf, atts.Ids[i], out bool grouped);
            if (!grouped)
            {
                int g0 = slot = attGroupCount++;
                attRep[g0] = i;
                attMaxTs[g0] = atts.TimestampsPgUs[i];
                attGames[g0] = atts.Counts[i];
                attSum[g0] = atts.SumScores[i];
                probeAttIds.Add(atts.Ids[i]);
                probeAttTypes.Add(atts.TypeIds[i]);
                probeAttSubjects.Add(atts.SubjectIds[i]);
                attGroupOfRow[i] = g0;
                continue;
            }
            int g = slot;
            attGroupOfRow[i] = g;
            if (atts.FoldReplayable[attRep[g]] != atts.FoldReplayable[i])
                throw new InvalidOperationException(
                    $"attestation {atts.Ids[i]} mixes replayable and transient observations in one working set");
            attGames[g] = AttestationMergeMath.SafeAddGames(attGames[g], atts.Counts[i]);
            attSum[g] = AttestationMergeMath.SafeAddScores(attSum[g], atts.SumScores[i]);
            if (atts.TimestampsPgUs[i] > attMaxTs[g])
            {
                attRep[g] = i;
                attMaxTs[g] = atts.TimestampsPgUs[i];
            }
        }

        // Per-phase round-trip counters (lock / journal / epoch / probe / copy / merge),
        // summed into the returned total. The probe fans across connections, so its
        // counter is updated atomically.
        int rtLock = 0, rtJournal = 0, rtEpoch = 0, rtProbe = 0, rtCopy = 0, rtMerge = 0;
        int eIns = 0, pIns = 0, aIns = 0;
        long aFold = 0, eSkip = 0, pSkip = 0;

        long prepMs = prepSw.ElapsedMilliseconds;
        _log.LogInformation(
            "WS_APPLY prep: {Ms:N0}ms (blobs {BlobMs:N0} + parse {ParseMs:N0} + ent-dedupe {EntDedupeMs:N0} + rest {RestMs:N0}; {E:N0}e→{EDistinct:N0} distinct/{P:N0}p/{A:N0}a)",
            prepMs, blobMs, parseMs - blobMs, entDedupeMs, prepMs - parseMs - entDedupeMs,
            ents.Ids.Count, distinctStagedEntities, phys.Ids.Count, atts.Ids.Count);
        preparationDiagnostic?.Complete();

        // Entity sort+pack overlaps verification. The outer control transaction
        // already holds the apply lock.
        Task<(byte[][] Payloads, int[] RowsByLane, int Groups)>? optimisticEntCopy = null;
        long firstEntBytes = TotalEntityBytes(ents, firstEntIdx);
        int optimisticGroups = ResolveCopyGroups(firstEntIdx.Count, firstEntBytes);
        if (optimisticGroups > 1 && phys.Ids.Count == 0 && atts.Ids.Count == 0)
        {
            var idxSnap = firstEntIdx;
            var entsSnap = ents;
            var blobsSnap = entBlobs;
            int groupsSnap = optimisticGroups;
            int modulusSnap = _leafModulus;
            optimisticEntCopy = Task.Run(() =>
            {
                var payloads = BuildSortedEntityPayloads(
                    blobsSnap, entsSnap, idxSnap, groupsSnap, modulusSnap, out var rowsByLane);
                return (payloads, rowsByLane, groupsSnap);
            }, ct);
        }

        PostgresCommitReceipt commit;
        try
        {
            rtLock++;
            commit = await ReadCommitSettingsAsync(conn, tx, Durability, ct);
            rtLock++;

            // Control-transaction epoch advance: nextval before any write. nextval
            // values are totally ordered, so v − 1 − baseline − ownBumpsSinceBaseline
            // counts advances by other transactions since this writer's last
            // post-commit baseline. −1 means no baseline yet (first apply of this
            // writer, or no sequence installed).
            long epochForeignDelta = -1;
            if (epochRoute)
            {
                await using var epoch = conn.CreateCommand();
                epoch.Transaction = tx;
                epoch.CommandText = SqlCatalog.Get("write.advance_epoch").Text;
                long bumped = (long)(await epoch.ExecuteScalarAsync(ct))!;
                rtEpoch++;
                if (_epochAfterLastCommit >= 0)
                    epochForeignDelta = bumped - 1 - _epochAfterLastCommit
                        - Interlocked.Read(ref _epochOwnBumpsSinceBaseline);
                // Count our own bump AFTER the delta: v itself is excluded by v − 1.
                Interlocked.Increment(ref _epochOwnBumpsSinceBaseline);
            }

            if (workingSetToken is Hash128 token)
            {
                await using var journal = conn.CreateCommand();
                journal.Transaction = tx;
                journal.CommandText =
                    "SELECT 1 FROM laplace.ingest_flush_journal WHERE working_set_id = $1";
                journal.Parameters.Add(new NpgsqlParameter
                { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                object? replay = await journal.ExecuteScalarAsync(ct);
                rtJournal++;
                if (replay is not null)
                {
                    if (!completions.IsEmpty)
                    {
                        // The journal proves this working set's evidence committed, so
                        // its completion receipts are written without persisting again.
                        await completions.InsertAsync(conn, tx, ct).ConfigureAwait(false);
                        rtJournal++;
                        await CommitMeasuredAsync(tx, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                    }
                    _log.LogInformation(
                        "WORKING_SET_REPLAY token={Token} already journaled — skipping complete admission",
                        token);
                    return (0, 0, 0, 0, 0, 0, rtLock + rtJournal + rtEpoch, true, commit, copyTransactions);
                }

                await InsertJournalReceiptAsync(
                    conn, tx, token, workingSetSource, workingSetSources,
                    receiptKind: "applied", ct)
                    .ConfigureAwait(false);
                rtJournal++;
            }

            var probePhysIdsUse = probePhysIds;

            // Probes fan out across pooled connections. This is sound under the
            // held advisory lock: every snapshot starts after the lock was
            // acquired, so anything a prior applier committed is visible.
            using var verificationDiagnostic = MeasureApplyPhase("presence-verification");
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();

            // Empty-relation skip (sound only under the apply advisory lock): if the
            // physicalities or attestations relation has no rows, every staged id for
            // it is absent and the bitmap probe is not run. Entity presence always goes
            // through the id bitmap.
            //
            // These probes run on pooled connections, not the control transaction, so
            // each releases its AccessShare locks when it completes; visibility is the
            // same because every snapshot starts after the advisory lock was acquired.
            long physEmptySkip = 0, attEmptySkip = 0;
            // Canonical entity storage is HASH(id). Tier is altitude, not
            // identity, and cannot participate in row presence or partition routing.
            var probeEntIdsUse = new List<Hash128>(firstEntIdx.Count);
            for (int k = 0; k < firstEntIdx.Count; k++)
                probeEntIdsUse.Add(ents.Ids[firstEntIdx[k]]);

            if (probePhysIdsUse.Count > 0)
            {
                rtProbe++;
                if (!await RelationHasRowsAsync(_ds, "physicalities", ct))
                {
                    physEmptySkip = probePhysIdsUse.Count;
                    probePhysIdsUse = new List<Hash128>();
                }
            }

            // Entity and physicality ids probe in staged order. The native probe
            // routes each id to its HASH(id) leaf and the btree array scan sorts
            // and dedups that leaf's keys, so every leaf is walked forward.
            // Entities and physicalities probe concurrently. The attestation
            // probe waits on the ENTITY result only for ordering; every staged
            // attestation is probed.
            //
            // The "novel by construction" shortcut that used to live here is
            // GONE (2026-07-21). It skipped the probe for any attestation whose
            // subject/object/context entity looked novel, reasoning that a novel
            // entity implies no committed attestation can embed it, and asserted
            // those rows were new. MEASURED on the OMW seed: one apply declared
            // 1,532,066 attestations novel-by-construction and the COPY died on
            //   23505 duplicate key ... attestations_r_has_language_h1_pkey
            // The retry, probing the same batch with the shortcut inactive, found
            // 3,495,027 PRESENT and only 826,624 genuinely novel. The inference
            // was wrong by millions of rows.
            //
            // Its failure mode is the worst kind: not a slow path but a hard
            // ingest abort plus a whole-batch retry (~5 minutes re-done, then the
            // run dies anyway). An unsound novelty proof cannot be traded for
            // probe time — COPY has no ON CONFLICT, so being wrong is fatal,
            // while being slow is merely slow. Probe cost for the rows it used to
            // skip is roughly +40% on the attestation leg of the verify.
            //
            // If this is ever reinstated it needs a proof that survives
            // multi-batch runs and retries, plus an assertion sampling skipped
            // ids against the DB — not a comment asserting the invariant holds.
            // Attestation prep before the fan: empty-relation skip + sort. The three
            // bitmap probes then run concurrently — attestation no longer waits on
            // the entity result (novel-by-construction shortcut is gone; see comment
            // block above). Verify wall becomes max(ent,phys,att).
            var probeAttIdsUse = probeAttIds;
            var probeAttTypesUse = probeAttTypes;
            var probeAttSubjectsUse = probeAttSubjects;
            if (probeAttIdsUse.Count > 0)
            {
                rtProbe++;
                if (!await RelationHasRowsAsync(_ds, "attestations", ct))
                {
                    attEmptySkip = probeAttIdsUse.Count;
                    probeAttIdsUse = new List<Hash128>();
                    probeAttTypesUse = new List<Hash128>();
                    probeAttSubjectsUse = new List<Hash128>();
                }
            }
            if (probeAttIdsUse.Count > 1)
            {
                var attIds = probeAttIdsUse;
                var attTypes = probeAttTypesUse;
                var attSubjects = probeAttSubjectsUse;
                var perm = BuildProbePermutation(attIds.Count, (a, b) =>
                {
                    int c = attTypes[a].CompareToBytewise(attTypes[b]);
                    return c != 0 ? c : attSubjects[a].CompareToBytewise(attSubjects[b]);
                });
                probeAttIdsUse = ApplyProbePermutation(attIds, perm);
                probeAttTypesUse = ApplyProbePermutation(attTypes, perm);
                probeAttSubjectsUse = ApplyProbePermutation(attSubjects, perm);
            }

            var entProbeTask = ProbePresentCoreAsync(
                "SELECT laplace.entities_stored_bitmap($1)", probeEntIdsUse,
                static (_, _, _) => { },
                r => Interlocked.Add(ref rtProbe, r), ct);
            // Id-only physicality probe: physicalities are partitioned by HASH(id),
            // so a Hilbert-keyed probe would search the wrong partition and report a
            // stored row absent.
            var physProbeTask = ProbePresentCoreAsync(
                "SELECT laplace.physicalities_exist_bitmap($1)", probePhysIdsUse,
                static (_, _, _) => { },
                r => Interlocked.Add(ref rtProbe, r), ct);
            var attProbeTask = ProbePresentKeyedParallelAsync(
                "laplace.attestations_exist_bitmap", probeAttIdsUse, probeAttTypesUse,
                probeAttSubjectsUse, r => Interlocked.Add(ref rtProbe, r), ct);

            await Task.WhenAll(entProbeTask, physProbeTask, attProbeTask).ConfigureAwait(false);
            var presentEntities = await entProbeTask.ConfigureAwait(false);
            var presentPhys = await physProbeTask.ConfigureAwait(false);
            var presentAtts = await attProbeTask.ConfigureAwait(false);
            // Tier-0 gated ids are present by the Unicode layer-completion marker.
            if (tier0Present is not null)
                foreach (var id in tier0Present) presentEntities.Add(id);
            _log.LogInformation(
                "WS_APPLY verify: {Entities:N0}e+{Phys:N0}p+{Atts:N0}a ids probed in {Ms:N0}ms "
                + "(skipped {T0:N0}e tier0-gate, {PEmpty:N0}p/{AEmpty:N0}a empty-relation; "
                + "present: {PresentE:N0}e/{PresentP:N0}p/{PresentA:N0}a; epoch foreign-delta {EpochForeignDelta})",
                probeEntIdsUse.Count, probePhysIdsUse.Count, probeAttIdsUse.Count, phaseSw.ElapsedMilliseconds,
                tier0Present?.Count ?? 0, physEmptySkip, attEmptySkip,
                presentEntities.Count, presentPhys.Count, presentAtts.Count, epochForeignDelta);
            verificationDiagnostic?.Complete();

            using var filteringDiagnostic = MeasureApplyPhase("copy-survivor-selection");

            // Entities: one deterministic representative per id, minus stored
            // rows. Kept rows are keyed by id so parallel COPY groups stay
            // uniform over HASH(id), and sorted ids walk each bucket's PK leaves
            // forward.
            List<KeptRow> keptEnts;
            byte[][]? prebuiltEntPayloads = null;
            int[]? prebuiltEntRowsByLane = null;
            int prebuiltEntGroups = 0;
            bool anyPresent = presentEntities.Count > 0;
            if (optimisticEntCopy is not null)
            {
                var prepared = await optimisticEntCopy.ConfigureAwait(false);
                if (!anyPresent)
                {
                    prebuiltEntPayloads = prepared.Payloads;
                    prebuiltEntRowsByLane = prepared.RowsByLane;
                    prebuiltEntGroups = prepared.Groups;
                    keptEnts = new List<KeptRow> { default };
                }
                else
                {
                    keptEnts = new List<KeptRow>(firstEntIdx.Count);
                    for (int k = 0; k < firstEntIdx.Count; k++)
                    {
                        int i = firstEntIdx[k];
                        var eid = ents.Ids[i];
                        if (presentEntities.Contains(eid))
                        { eSkip++; continue; }
                        keptEnts.Add(new KeptRow(
                            eid, CopyPartitionKey.ForEntityId(eid), ents.Rows[i], -1, 0));
                    }
                    if (eSkip == 0)
                    {
                        prebuiltEntPayloads = prepared.Payloads;
                        prebuiltEntRowsByLane = prepared.RowsByLane;
                        prebuiltEntGroups = prepared.Groups;
                        keptEnts = new List<KeptRow> { default };
                    }
                }
            }
            else
            {
                keptEnts = new List<KeptRow>(firstEntIdx.Count);
                for (int k = 0; k < firstEntIdx.Count; k++)
                {
                    int i = firstEntIdx[k];
                    var eid = ents.Ids[i];
                    if (presentEntities.Contains(eid))
                    { eSkip++; continue; }
                    keptEnts.Add(new KeptRow(
                        eid, CopyPartitionKey.ForEntityId(eid), ents.Rows[i], -1, 0));
                }
            }

            // Physicalities: first occurrence of each id, minus stored rows.
            // Groups are split by id, matching HASH(id) partitioning and PK(id):
            // ids are content hashes, so id-range groups are equal-sized and land
            // in disjoint PK leaf ranges. A Hilbert index is a curve position, not a
            // hash; splitting by it would pile clustered coordinates into one group.
            var keptPhys = new List<KeptRow>(phys.Rows.Count);
            var seenPhys = new HashSet<Hash128>(phys.Ids.Count);
            for (int i = 0; i < phys.Ids.Count; i++)
            {
                if (!seenPhys.Add(phys.Ids[i])) continue;
                var pid = phys.Ids[i];
                if (presentPhys.Contains(pid))
                { pSkip++; continue; }
                // Lane by id (uniform), ORDER by hilbert (coord GiST locality).
                keptPhys.Add(new KeptRow(
                    pid,
                    CopyPartitionKey.ForHilbertIndex(phys.HilbertKeys[i]),
                    phys.Rows[i], -1, 0));
            }

            // Physicality closure, counted per working set in memory: every entity this
            // apply writes should be realized by a physicality staged in the same set,
            // since a novel entity cannot already have a stored physicality. Novel
            // entities without one are counted as unplaced.
            {
                var placedEntities = new HashSet<Hash128>(phys.EntityIds);
                long unplaced = 0;
                for (int k = 0; k < firstEntIdx.Count; k++)
                {
                    var eid = ents.Ids[firstEntIdx[k]];
                    if (presentEntities.Contains(eid))
                        continue;
                    if (!placedEntities.Contains(eid)) unplaced++;
                }
                _closure.Record(firstEntIdx.Count, unplaced);
            }

            // The content-addressed five-tuple owns testimony identity. An
            // existing attestation is a replay even if dispatch boundaries or
            // the working-set token changed. Only novel identities may fold.
            var novelRepIdx = new List<int>(attGroupCount);
            for (int g = 0; g < attGroupCount; g++)
                if (!presentAtts.Contains(probeAttIds[g]))
                    novelRepIdx.Add(attRep[g]);
            novelRepIdx.Sort();
            var keptAtts = new List<KeptRow>(novelRepIdx.Count);
            for (int k = 0; k < novelRepIdx.Count; k++)
            {
                int i = novelRepIdx[k];
                int g = attGroupOfRow[i];
                bool collapsed = attGames[g] != atts.Counts[i] || attSum[g] != atts.SumScores[i];
                keptAtts.Add(new KeptRow(
                    atts.SubjectIds[i],
                    CopyPartitionKey.ForEntityId(atts.Ids[i]), atts.Rows[i],
                    collapsed ? attGames[g] : -1,
                    atts.CountValueOffsets[i],
                    collapsed ? attSum[g] : 0,
                    atts.SumScoreValueOffsets[i]));
            }

            int keptEntCount = prebuiltEntPayloads is not null
                ? firstEntIdx.Count - (int)eSkip
                : keptEnts.Count;
            long keptEntBytes = prebuiltEntPayloads is not null
                ? prebuiltEntPayloads.Sum(static p => (long)p.Length)
                : TotalKeptBytes(keptEnts);
            long keptPhysBytes = TotalKeptBytes(keptPhys);
            long keptAttBytes = TotalKeptBytes(keptAtts);
            _log.LogInformation(
                "WS_APPLY kept: {E:N0}e/{P:N0}p/{A:N0}a novel after verify in {Ms:N0}ms since verify-start",
                keptEntCount, keptPhys.Count, keptAtts.Count, phaseSw.ElapsedMilliseconds);
            filteringDiagnostic?.Complete();

            using var copyDiagnostic = MeasureApplyPhase("copy-and-copy-transaction-commits");

            bool parallelCopy = ApplyParallelism > 1
                && (ResolveCopyGroups(keptEntCount, keptEntBytes) > 1
                    || ResolveCopyGroups(keptPhys.Count, keptPhysBytes) > 1
                    || ResolveCopyGroups(keptAtts.Count, keptAttBytes) > 1);

            if (!parallelCopy)
            {
                // Small applies stay fully atomic inside the control transaction.
                if (keptEnts.Count > 0 || keptPhys.Count > 0 || keptAtts.Count > 0)
                    copyTransactions.StartControl();
                if (keptEnts.Count > 0)
                {
                    await CopyKeptAsync(conn, "entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, 0, keptEnts.Count, ct);
                    eIns = keptEnts.Count;
                    rtCopy++;
                }
                if (keptPhys.Count > 0)
                {
                    await CopyKeptAsync(conn, "physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, 0, keptPhys.Count, ct);
                    pIns = keptPhys.Count;
                    rtCopy++;
                }
                if (keptAtts.Count > 0)
                {
                    await CopyKeptAsync(conn, "attestations", IntentStageTable.Attestations,
                        attBlobs, keptAtts, 0, keptAtts.Count, ct);
                    aIns = keptAtts.Count;
                    rtCopy++;
                }
            }
            else
            {
                // Bulk COPY fans out across connections that each hold a disjoint
                // primary-key range. Indexes stay online throughout. Per-table barriers
                // make referenced rows durable before their referencers. The control
                // transaction holds the advisory lock across the whole window, so no
                // other applier interleaves; a crash mid-phase leaves no flush-journal
                // receipt, and the retry's verification subtracts what already landed.

                // Entities complete first, so a committed attestation implies its
                // working set's entities are committed. Physicalities and
                // attestations do not depend on each other and may overlap.
                if (prebuiltEntPayloads is not null)
                {
                    rtCopy += await CopyPayloadsParallelAsync(
                        "entities", IntentStageTable.Entities,
                        keptEntCount, prebuiltEntGroups, prebuiltEntPayloads, prebuiltEntRowsByLane!,
                        sortMs: 0, copyTransactions, ct);
                }
                else
                {
                    rtCopy += await CopyPhaseParallelAsync("entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, copyTransactions, ct);
                }
                eIns = keptEntCount;
                // A claimed working-set token is the exactly-once boundary for
                // evidence and the standing folded from it. Entity and physicality
                // copies may commit independently: their content-addressed ids are
                // re-verified and subtracted on a retry. Attestation COPY and its
                // consensus fold commit together in the control transaction, so a
                // retry cannot mistake unfolded testimony for a completed observation.
                if (workingSetToken is not null)
                {
                    rtCopy += await CopyPhaseParallelAsync("physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, copyTransactions, ct);
                    pIns = keptPhys.Count;
                    if (keptAtts.Count > 0)
                    {
                        copyTransactions.StartControl();
                        await CopyKeptAsync(conn, "attestations", IntentStageTable.Attestations,
                            attBlobs, keptAtts, 0, keptAtts.Count, ct);
                        aIns = keptAtts.Count;
                        rtCopy++;
                    }
                }
                else
                {
                    var physCopyTask = CopyPhaseParallelAsync("physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, copyTransactions, ct);
                    var attCopyTask = CopyPhaseParallelAsync("attestations", IntentStageTable.Attestations,
                        attBlobs, keptAtts, copyTransactions, ct);
                    await Task.WhenAll(physCopyTask, attCopyTask);
                    rtCopy += physCopyTask.Result + attCopyTask.Result;
                    pIns = keptPhys.Count;
                    aIns = keptAtts.Count;
                }

            }

            copyDiagnostic?.Complete();

            // Fold in sets: the consensus participant (supplied by the accumulating
            // writer for a freshly claimed working set) folds the novel attestations
            // in this same transaction as the evidence and the journal receipt, so a
            // failure leaves no receipt and a retry that sees the receipt cannot refold.
            if (transactionParticipant is not null && workingSetToken is not null)
            {
                using var participantDiagnostic = MeasureApplyPhase("consensus-acceptance-participant");
                var acceptedRows = new List<AttestationRow>(novelRepIdx.Count);
                foreach (int i in novelRepIdx)
                {
                    int g = attGroupOfRow[i];
                    acceptedRows.Add(decodedAtts![i] with
                    {
                        ObservationCount = attGames[g],
                        SumScoreFp1e9 = attSum[g],
                        LastObservedAtUnixUs = decodedAtts[i].LastObservedAtUnixUs
                            + (attMaxTs[g] - atts.TimestampsPgUs[i]),
                    });
                }
                await transactionParticipant(conn, tx,
                    new WorkingSetAcceptedEvidence(
                        novelRepIdx.Select(i => atts.Ids[i]).ToHashSet())
                    {
                        Rows = acceptedRows,
                    }, ct);
                participantDiagnostic?.Complete();
            }

            // Completion receipts commit with the control transaction that persists this
            // evidence: a failed or rolled-back apply leaves no completion row.
            if (!completions.IsEmpty)
            {
                using var completionDiagnostic = MeasureApplyPhase("completion-state");
                await completions.InsertAsync(conn, tx, ct).ConfigureAwait(false);
                rtJournal++;
                completionDiagnostic?.Complete();
            }

            await CommitMeasuredAsync(tx, ct);
            copyTransactions.CommitControl();
            commit = commit with { WriteCommitAcknowledged = workingSetToken is not null
                || eIns > 0 || pIns > 0 || aIns > 0 || aFold > 0 };

            // Epoch baseline for the next apply's foreign delta: last_value read after
            // every advance this apply made has committed, so any later advance is
            // another transaction's. Reading the sequence relation needs no nextval and
            // takes no lock. A concurrent advance between the merge commits and this
            // read is absorbed into the baseline; only the logged delta is affected.
            if (epochRoute)
            {
                await using var last = conn.CreateCommand();
                last.CommandText = "SELECT last_value FROM laplace.apply_write_epoch";
                _epochAfterLastCommit = (long)(await last.ExecuteScalarAsync(ct))!;
                Interlocked.Exchange(ref _epochOwnBumpsSinceBaseline, 0);
                rtEpoch++;
            }
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }
        int rt = rtLock + rtJournal + rtEpoch + rtProbe + rtCopy + rtMerge;
        _log.LogInformation(
            "WS_APPLY round-trips: {Total} = {Lock} lock + {Journal} journal + {Epoch} epoch + {Probe} probe + {Copy} copy + {Merge} merge "
            + "({E:N0}e/{P:N0}p/{A:N0}a novel, {Fold:N0} merged)",
            rt, rtLock, rtJournal, rtEpoch, rtProbe, rtCopy, rtMerge, eIns, pIns, aIns, aFold);
        return (eIns, pIns, aIns, aFold, eSkip, pSkip, rt, false, commit, copyTransactions);
    }

    private sealed class CopyTransactionCounts
    {
        internal int Started;
        internal int Committed;
        private bool _controlContainsCopy;
        internal void StartControl()
        {
            if (_controlContainsCopy) return;
            _controlContainsCopy = true;
            Interlocked.Increment(ref Started);
        }
        internal void CommitControl()
        {
            if (_controlContainsCopy) Interlocked.Increment(ref Committed);
        }
    }

    internal static string TransactionGucs(PostgresWriteDurability durability) => durability switch
    {
        PostgresWriteDurability.Asynchronous => "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = off; SET LOCAL jit = off; ",
        PostgresWriteDurability.Synchronous => "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = on; SET LOCAL jit = off; ",
        _ => throw new ArgumentOutOfRangeException(nameof(durability)),
    };

    internal static async Task<PostgresCommitReceipt> ReadCommitSettingsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgresWriteDurability durability,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlCatalog.Get("write.commit_settings").Text;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("PostgreSQL commit settings are unavailable.");
        var receipt = new PostgresCommitReceipt(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), false);
        if (receipt.SynchronousCommit != (durability == PostgresWriteDurability.Synchronous ? "on" : "off"))
            throw new InvalidOperationException("PostgreSQL did not apply the selected commit acknowledgement mode.");
        if (durability == PostgresWriteDurability.Synchronous && (!receipt.Fsync || !receipt.FullPageWrites))
            throw new InvalidOperationException("Synchronous recording requires PostgreSQL fsync and full_page_writes enabled.");
        return receipt;
    }

    private static List<(IntPtr Ptr, long Len)> CollectBlobs(
        IReadOnlyList<IntentStage> stages, IntentStageTable table, int expectedFields, string tableName)
    {
        var blobs = new List<(IntPtr, long)>(stages.Count);
        foreach (var s in stages)
        {
            int rowCount = table switch
            {
                IntentStageTable.Entities => s.EntityCount,
                IntentStageTable.Physicalities => s.PhysicalityCount,
                _ => s.AttestationCount,
            };
            if (rowCount == 0) continue;
            (IntPtr ptr, long len) = s.TupleBuffer(table);
            if (ptr == IntPtr.Zero || len <= 0) continue;
            if (CopyBlobValidator.Enabled)
                CopyBlobValidator.Validate(ptr, len, expectedFields, tableName, rowCount);
            blobs.Add((ptr, len));
        }
        return blobs;
    }

    /// <summary>
    /// Returns one deterministic row index per staged entity id, in bytewise id
    /// order: the representative is the lowest tier, then the bytewise-smallest
    /// type_id, independent of arrival order. With the tier-0 gate on, an id with
    /// any tier-0 row goes to <paramref name="tier0Present"/> instead and skips
    /// the probe.
    /// </summary>
    internal static List<int> DistinctEntityRowIndices(
        CopyTupleParser.EntityRows ents, bool tier0Gate, out List<Hash128>? tier0Present)
    {
        var ids = CollectionsMarshal.AsSpan(ents.Ids);
        var tiers = CollectionsMarshal.AsSpan(ents.Tiers);
        var types = CollectionsMarshal.AsSpan(ents.TypeIds);
        var best = new Dictionary<Hash128, int>(ids.Length);
        HashSet<Hash128>? tier0Ids = tier0Gate ? new HashSet<Hash128>() : null;

        for (int i = 0; i < ids.Length; i++)
        {
            Hash128 id = ids[i];
            if (tier0Gate && tiers[i] == 0) tier0Ids!.Add(id);
            ref int prior = ref CollectionsMarshal.GetValueRefOrAddDefault(best, id, out bool seen);
            if (!seen
                || tiers[i] < tiers[prior]
                || (tiers[i] == tiers[prior]
                    && types[i].CompareToBytewise(types[prior]) < 0))
                prior = i;
        }

        // Survivors keep first-seen order: presence routes each id to its hash leaf
        // and every COPY lane sorts its own rows, so no global id order is needed.
        int n = best.Count;
        var selected = new List<int>(n);
        tier0Present = tier0Gate ? new List<Hash128>() : null;
        foreach (int row in best.Values)
        {
            if (tier0Gate && tier0Ids!.Contains(ids[row]))
            {
                tier0Present!.Add(ids[row]);
                continue;
            }
            selected.Add(row);
        }
        return selected;
    }

    private static async Task<bool> RelationHasRowsAsync(
        NpgsqlDataSource ds, string relation, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        // Relation name is an internal constant (entities/physicalities/attestations), not user input.
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM laplace.{relation} LIMIT 1)";
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is true;
    }

    private readonly record struct EntityInvertResult(
        List<int> RemainingIdx, long Resolved, int RoundTrips);

    /// <summary>
    /// Per-tier presence resolution: for each tier where fewer rows are stored than
    /// this batch stages, load that tier's stored ids and resolve membership locally;
    /// other tiers stay on the bitmap probe. Not used by the apply path, whose
    /// presence is id-only over HASH(id) because tier is not identity.
    /// <paramref name="rowIdx"/> are indices into <paramref name="ents"/>.
    /// </summary>
    private static async Task<EntityInvertResult> InvertEntityTiersBySmallerSideAsync(
        NpgsqlDataSource ds,
        CopyTupleParser.EntityRows ents, List<int> rowIdx, HashSet<Hash128> presentOut,
        CancellationToken ct)
    {
        if (rowIdx.Count == 0)
            return new EntityInvertResult(rowIdx, 0, 0);

        var stagedPerTier = new Dictionary<short, int>();
        for (int k = 0; k < rowIdx.Count; k++)
        {
            short tier = ents.Tiers[rowIdx[k]];
            stagedPerTier.TryGetValue(tier, out int n);
            stagedPerTier[tier] = n + 1;
        }
        // Bounded count, not a census: the number only feeds `have < staged`, so
        // counting stops at `staged` (LIMIT need). At or above the bound the count
        // equals staged and the test is false; below it the true count is returned.
        var tierArr = new short[stagedPerTier.Count];
        var needArr = new int[stagedPerTier.Count];
        {
            int ti = 0;
            foreach (var (tier, staged) in stagedPerTier)
            {
                tierArr[ti] = tier;
                needArr[ti] = staged;
                ti++;
            }
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandTimeout = 0;
            countCmd.CommandText =
                "SELECT t.tier, (SELECT count(*)::bigint FROM ("
                + "  SELECT 1 FROM laplace.entities e WHERE e.tier = t.tier LIMIT t.need) z) "
                + "FROM unnest($1::smallint[], $2::int[]) AS t(tier, need)";
            countCmd.Parameters.Add(new NpgsqlParameter
            { Value = tierArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
            countCmd.Parameters.Add(new NpgsqlParameter
            { Value = needArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            await countCmd.PrepareAsync(ct);
            var presentCount = new Dictionary<short, long>();
            await using (var reader = await countCmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    presentCount[reader.GetInt16(0)] = reader.GetInt64(1);

            var invertTiers = new HashSet<short>();
            foreach (var (tier, staged) in stagedPerTier)
            {
                presentCount.TryGetValue(tier, out long have);
                if (have < staged) invertTiers.Add(tier);
            }
            if (invertTiers.Count == 0)
                return new EntityInvertResult(rowIdx, 0, 1);

            var invertArr = invertTiers.ToArray();
            await using var loadCmd = conn.CreateCommand();
            loadCmd.CommandTimeout = 0;
            loadCmd.CommandText = "SELECT e.id FROM laplace.entities e WHERE e.tier = ANY($1)";
            loadCmd.Parameters.Add(new NpgsqlParameter
            { Value = invertArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
            await loadCmd.PrepareAsync(ct);
            var presentInLeaf = new HashSet<Hash128>();
            await using (var idReader = await loadCmd.ExecuteReaderAsync(ct))
            {
                while (await idReader.ReadAsync(ct))
                {
                    var raw = (byte[])idReader[0];
                    if (raw.Length >= 16) presentInLeaf.Add(Hash128.FromBytes(raw));
                }
            }

            // Every staged tier inverted: no ids remain for the probe; only
            // mark the present hits.
            if (invertTiers.Count == stagedPerTier.Count)
            {
                if (presentInLeaf.Count > 0)
                {
                    for (int k = 0; k < rowIdx.Count; k++)
                    {
                        var id = ents.Ids[rowIdx[k]];
                        if (presentInLeaf.Contains(id)) presentOut.Add(id);
                    }
                }
                return new EntityInvertResult(new List<int>(), rowIdx.Count, 2);
            }

            var remainIdx = new List<int>();
            long resolved = 0;
            for (int k = 0; k < rowIdx.Count; k++)
            {
                int i = rowIdx[k];
                if (!invertTiers.Contains(ents.Tiers[i]))
                {
                    remainIdx.Add(i);
                    continue;
                }
                resolved++;
                if (presentInLeaf.Contains(ents.Ids[i])) presentOut.Add(ents.Ids[i]);
            }
            return new EntityInvertResult(remainIdx, resolved, 2);
        }
    }

    /// <summary>
    /// Shared chunked, connection-parallel presence probe. Sends the ids in
    /// sized chunks as $1 (bytea[]); <paramref name="bindKeys"/> adds the target
    /// table's partition-key arrays for the same [start, start+n) window. The
    /// returned bitmap is decoded back to the ids found present. Every probe
    /// shape (id-only and keyed) uses this one implementation.
    /// </summary>
    private async Task<HashSet<Hash128>> ProbePresentCoreAsync(
        string commandText, IReadOnlyList<Hash128> ids,
        Action<NpgsqlParameterCollection, int, int> bindKeys,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;

        // Memory bounds the chunk; the apply connections share the set so every
        // backend probes its slice concurrently.
        int probeChunkIds = Math.Min(ApplySizing.ProbeChunkIds,
            Math.Max(1, (ids.Count + ApplyParallelism - 1) / ApplyParallelism));
        int chunkCount = (ids.Count + probeChunkIds - 1) / probeChunkIds;
        var perChunk = new List<Hash128>[chunkCount];

        async Task ProbeChunkAsync(int c, CancellationToken token)
        {
            int start = c * probeChunkIds;
            int n = Math.Min(probeChunkIds, ids.Count - start);
            var chunk = new byte[n][];
            for (int i = 0; i < n; i++) chunk[i] = ids[start + i].ToBytes();

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = commandText;
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            bindKeys(cmd.Parameters, start, n);
            await cmd.PrepareAsync(token);
            var bm = await cmd.ExecuteScalarAsync(token) as byte[] ?? Array.Empty<byte>();

            var hits = new List<Hash128>();
            long bits = (long)bm.Length * 8;
            for (int i = 0; i < n; i++)
                if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0)
                    hits.Add(ids[start + i]);
            perChunk[c] = hits;
        }

        if (chunkCount == 1)
        {
            await ProbeChunkAsync(0, ct);
        }
        else
        {
            // Chunk workers are bounded by ApplyParallelism.
            int workers = Math.Min(chunkCount, ApplyParallelism);
            int next = -1;
            await CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
            {
                for (int c = Interlocked.Increment(ref next); c < chunkCount;
                     c = Interlocked.Increment(ref next))
                    await ProbeChunkAsync(c, token);
            }, ct);
        }

        foreach (var hits in perChunk)
            if (hits is not null)
                foreach (var id in hits) present.Add(id);
        addRoundTrips(chunkCount);
        return present;
    }

    /// <summary>Presence probe that passes each id's staged tier as a second
    /// key array ($2 smallint[]) alongside the ids.</summary>
    private Task<HashSet<Hash128>> ProbePresentTieredParallelAsync(
        string function, IReadOnlyList<Hash128> ids, IReadOnlyList<short> tiers,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        if (tiers.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {tiers.Count} tiers");
        return ProbePresentCoreAsync($"SELECT {function}($1, $2)", ids,
            (parameters, start, n) =>
            {
                var chunk = new short[n];
                for (int i = 0; i < n; i++) chunk[i] = tiers[start + i];
                parameters.Add(new NpgsqlParameter
                { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
            }, addRoundTrips, ct);
    }

    // Identity permutation sorted by a partition-key comparison over indices, so all parallel
    // probe arrays can be reordered together for forward index walks (see the call site).
    private static int[] BuildProbePermutation(int count, Comparison<int> byKey)
    {
        var perm = new int[count];
        for (int i = 0; i < count; i++) perm[i] = i;
        Array.Sort(perm, byKey);
        return perm;
    }

    private static List<T> ApplyProbePermutation<T>(IReadOnlyList<T> src, int[] perm)
    {
        var reordered = new List<T>(src.Count);
        for (int i = 0; i < perm.Length; i++) reordered.Add(src[perm[i]]);
        return reordered;
    }

    /// <summary>Triple-keyed presence probe for attestations, which are partitioned
    /// LIST(type_id) -> HASH(subject_id): passing type and subject lets the probe prune
    /// to one leaf per id, where an id-only probe would descend every leaf's index.</summary>
    private Task<HashSet<Hash128>> ProbePresentKeyedParallelAsync(
        string function, IReadOnlyList<Hash128> ids, IReadOnlyList<Hash128> typeIds,
        IReadOnlyList<Hash128> subjectIds, Action<int> addRoundTrips, CancellationToken ct)
    {
        if (typeIds.Count != ids.Count || subjectIds.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {typeIds.Count} types / {subjectIds.Count} subjects");
        return ProbePresentCoreAsync($"SELECT {function}($1, $2, $3)", ids,
            (parameters, start, n) =>
            {
                var chunkTypes = new byte[n][];
                var chunkSubjects = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    chunkTypes[i] = typeIds[start + i].ToBytes();
                    chunkSubjects[i] = subjectIds[start + i].ToBytes();
                }
                parameters.Add(new NpgsqlParameter
                { Value = chunkTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                parameters.Add(new NpgsqlParameter
                { Value = chunkSubjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            }, addRoundTrips, ct);
    }

    /// <summary>
    /// 16-octet memcmp key matching Postgres bytea/btree order for the column
    /// that partitions parallel COPY groups. For entities/attestations that is
    /// the row id (<see cref="Hash128"/>); for physicalities it is the 128-bit
    /// hilbert curve index. Storage here is wire order only — not a claim that
    /// hilbert is a hash.
    /// </summary>
    private readonly struct CopyPartitionKey
    {
        private readonly Hash128 _wire;
        private CopyPartitionKey(Hash128 wire) => _wire = wire;
        public Hash128 Wire => _wire;
        public static CopyPartitionKey ForEntityId(Hash128 id) => new(id);
        public static CopyPartitionKey ForHilbertIndex(Hilbert128 index)
        {
            Span<byte> pack = stackalloc byte[16];
            index.WriteBytes(pack);
            return new(Hash128.FromBytes(pack));
        }
        public int CompareToBytewise(CopyPartitionKey other) =>
            _wire.CompareToBytewise(other._wire);
    }

    /// <summary>Pre-reversed 128-bit key for Array.Sort (memcmp order, no per-compare work).</summary>
    private readonly record struct CopySortKey(ulong HiBe, ulong LoBe) : IComparable<CopySortKey>
    {
        public static CopySortKey FromWire(Hash128 wire) => new(
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(wire.Hi),
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(wire.Lo));
        public int CompareTo(CopySortKey other)
        {
            int c = HiBe.CompareTo(other.HiBe);
            return c != 0 ? c : LoBe.CompareTo(other.LoBe);
        }
    }

    /// <summary>
    /// <para><b>RouteKey</b> is the value of the table's HASH partition column (the id for
    /// entities and physicalities, the subject for attestations). It names the leaf that
    /// owns the row, and each leaf rides exactly one COPY connection.</para>
    ///
    /// <para><b>OrderKey</b> is the sort within a leaf, which decides the order index pages
    /// are touched: the id for the btree tables, the hilbert index for physicalities,
    /// whose contended index is the coord GiST.</para>
    ///
    /// <para>Patch/PatchSum carry a duplicate-collapsed group's summed games/sum_score for
    /// the representative row (Patch = -1 means unpatched).</para>
    /// </summary>
    private readonly record struct KeptRow(
        Hash128 RouteKey, CopyPartitionKey OrderKey, StagedRowRef Row,
        long Patch, int CountOff, long PatchSum = 0, int SumOff = 0);

    /// <summary>
    /// CLR array-addressability ceiling for the one contiguous payload PackFiltered
    /// builds. Measured in bytes, never rows: a single row can carry a very large
    /// trajectory.
    /// </summary>
    private static readonly long MaxCopyPayloadBytes = Array.MaxLength;

    /// <summary>
    /// Splits a kept range into byte-bounded COPY payloads. PackFiltered packs one call
    /// into a single byte[] and refuses past the array ceiling, so chunks are bounded by
    /// bytes: a few rows can carry gigabytes of trajectory. Each chunk gets its own COPY
    /// stream through CopyFilteredAsync; the wire protocol is unchanged.
    /// </summary>
    private static async Task CopyKeptAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        int end = start + count;
        for (int i = start; i < end;)
        {
            long bytes = 0;
            int chunk = 0;
            while (i + chunk < end)
            {
                long len = kept[i + chunk].Row.Length;
                // Always take at least one row. A single row over the ceiling cannot
                // be split here; it must reach PackFiltered and fail with that
                // function's explicit size message rather than spin taking zero rows.
                if (chunk > 0 && bytes + len > MaxCopyPayloadBytes) break;
                bytes += len;
                chunk++;
            }
            await CopyKeptChunkAsync(conn, tableName, table, blobs, kept, i, chunk, ct);
            i += chunk;
        }
    }

    private static async Task CopyKeptChunkAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        var rows = new List<StagedRowRef>(count);
        long[]? patches = null;
        int[]? countOffs = null;
        long[]? sumPatches = null;
        int[]? sumOffs = null;
        bool anyPatch = false;
        for (int i = start; i < start + count; i++)
            if (kept[i].Patch >= 0) { anyPatch = true; break; }
        if (anyPatch)
        {
            patches = new long[count];
            countOffs = new int[count];
            sumPatches = new long[count];
            sumOffs = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            var k = kept[start + i];
            rows.Add(k.Row);
            if (patches is not null)
            {
                patches[i] = k.Patch;
                countOffs![i] = k.CountOff;
                sumPatches![i] = k.PatchSum;
                sumOffs![i] = k.SumOff;
            }
        }
        await CopyFilteredAsync(conn, tableName, table, blobs, rows, patches, countOffs, sumPatches, sumOffs, ct);
    }

    private static int ResolveCopyGroups(int rowCount, long payloadBytes)
    {
        // Each group owns whole leaves (RouteLeaves). Fanout is
        // justified by finalized payload bytes: another connection must receive at
        // least one transport buffer/page and one row. No row-count crossover or
        // host-specific worker cap survives here; topology owns the ceiling.
        return IngestSizing.ResolveCopyConnections(
            rowCount, payloadBytes, ApplyParallelism, ApplySizing.CopyStartupBytes);
    }

    private static long TotalEntityBytes(
        CopyTupleParser.EntityRows entities, IReadOnlyList<int> indices)
    {
        long bytes = 0;
        for (int i = 0; i < indices.Count; i++)
            bytes = checked(bytes + entities.Rows[indices[i]].Length);
        return bytes;
    }

    private static long TotalKeptBytes(IReadOnlyList<KeptRow> kept)
    {
        long bytes = 0;
        for (int i = 0; i < kept.Count; i++)
            bytes = checked(bytes + kept[i].Row.Length);
        return bytes;
    }

    /// <summary>
    /// Assign whole leaves to COPY lanes: largest leaf first to the least-loaded lane.
    /// Each backend then writes its own leaves, their rows contiguous, and no two
    /// backends insert into the same btree.
    /// </summary>
    internal static int[] LaneOfLeaf(ReadOnlySpan<int> rowsPerLeaf, int groups)
    {
        var lanes = new int[rowsPerLeaf.Length];
        if (groups <= 1) return lanes;
        var order = new int[rowsPerLeaf.Length];
        var sizes = new int[rowsPerLeaf.Length];
        for (int leaf = 0; leaf < order.Length; leaf++)
        {
            order[leaf] = leaf;
            sizes[leaf] = -rowsPerLeaf[leaf];
        }
        Array.Sort(sizes, order);
        var load = new long[groups];
        foreach (int leaf in order)
        {
            int lane = 0;
            for (int g = 1; g < groups; g++)
                if (load[g] < load[lane]) lane = g;
            lanes[leaf] = lane;
            load[lane] += rowsPerLeaf[leaf];
        }
        return lanes;
    }

    /// <summary>Owning leaf of each route key and the lane of each row.</summary>
    private static (int[] Leaf, int[] Lane, int[] Counts) RouteLeaves(
        ReadOnlySpan<Hash128> routeKeys, int modulus, int groups)
    {
        var leaf = new int[routeKeys.Length];
        PartitionRoute.Remainders(routeKeys, modulus, leaf);
        var rowsPerLeaf = new int[modulus];
        foreach (int l in leaf) rowsPerLeaf[l]++;
        int[] laneOfLeaf = LaneOfLeaf(rowsPerLeaf, groups);
        var lane = new int[leaf.Length];
        var counts = new int[Math.Max(1, groups)];
        for (int i = 0; i < leaf.Length; i++)
        {
            lane[i] = laneOfLeaf[leaf[i]];
            counts[lane[i]]++;
        }
        return (leaf, lane, counts);
    }

    /// <summary>Leaf first, then the order key within the leaf.</summary>
    private readonly record struct LeafOrderKey(int Leaf, CopySortKey Order) : IComparable<LeafOrderKey>
    {
        public int CompareTo(LeafOrderKey other)
        {
            int c = Leaf.CompareTo(other.Leaf);
            return c != 0 ? c : Order.CompareTo(other.Order);
        }
    }

    private static byte[][] BuildSortedEntityPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        CopyTupleParser.EntityRows ents, List<int> firstIdx, int groups, int modulus,
        out int[] rowsByLane)
    {
        int rowCount = firstIdx.Count;
        var routes = new Hash128[rowCount];
        for (int k = 0; k < rowCount; k++) routes[k] = ents.Ids[firstIdx[k]];
        var (leafOf, groupOf, counts) = RouteLeaves(routes, modulus, groups);
        var keysAll = new LeafOrderKey[rowCount];
        for (int k = 0; k < rowCount; k++)
            keysAll[k] = new LeafOrderKey(leafOf[k], CopySortKey.FromWire(routes[k]));
        var groupRefs = new StagedRowRef[groups][];
        var groupKeys = new LeafOrderKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRefs[g] = new StagedRowRef[counts[g]];
            groupKeys[g] = new LeafOrderKey[counts[g]];
        }
        for (int k = 0; k < rowCount; k++)
        {
            int g = groupOf[k];
            int o = next[g]++;
            groupRefs[g][o] = ents.Rows[firstIdx[k]];
            groupKeys[g][o] = keysAll[k];
        }
        var payloads = new byte[groups][];
        Parallel.For(0, groups, g =>
        {
            Array.Sort(groupKeys[g], groupRefs[g]);
            payloads[g] = groupRefs[g].Length == 0
                ? Array.Empty<byte>()
                : CopyTupleParser.PackFiltered(blobs, groupRefs[g]);
        });
        rowsByLane = counts;
        return payloads;
    }

    private static byte[][] BuildSortedCopyPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, int groups, int modulus,
        out int[] rowsByLane)
    {
        int rowCount = kept.Count;
        var routes = new Hash128[rowCount];
        for (int i = 0; i < rowCount; i++) routes[i] = kept[i].RouteKey;
        var (leafOf, groupOf, counts) = RouteLeaves(routes, modulus, groups);
        var keysAll = new LeafOrderKey[rowCount];
        for (int i = 0; i < rowCount; i++)
            keysAll[i] = new LeafOrderKey(leafOf[i], CopySortKey.FromWire(kept[i].OrderKey.Wire));
        var groupRows = new KeptRow[groups][];
        var groupKeys = new LeafOrderKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRows[g] = new KeptRow[counts[g]];
            groupKeys[g] = new LeafOrderKey[counts[g]];
        }
        for (int i = 0; i < rowCount; i++)
        {
            int g = groupOf[i];
            int o = next[g]++;
            groupRows[g][o] = kept[i];
            groupKeys[g][o] = keysAll[i];
        }
        var payloads = new byte[groups][];
        Parallel.For(0, groups, g =>
        {
            Array.Sort(groupKeys[g], groupRows[g]);
            var rows = groupRows[g];
            if (rows.Length == 0) { payloads[g] = Array.Empty<byte>(); return; }
            var refs = new StagedRowRef[rows.Length];
            for (int i = 0; i < rows.Length; i++) refs[i] = rows[i].Row;
            long[]? patches = null;
            int[]? countOffs = null;
            long[]? sumPatches = null;
            int[]? sumOffs = null;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Patch < 0) continue;
                patches = new long[rows.Length];
                countOffs = new int[rows.Length];
                sumPatches = new long[rows.Length];
                sumOffs = new int[rows.Length];
                Array.Fill(patches, -1);
                for (int j = 0; j < rows.Length; j++)
                {
                    patches[j] = rows[j].Patch;
                    countOffs[j] = rows[j].CountOff;
                    sumPatches[j] = rows[j].PatchSum;
                    sumOffs[j] = rows[j].SumOff;
                }
                break;
            }
            payloads[g] = CopyTupleParser.PackFiltered(
                blobs, refs, patches, countOffs, sumPatches, sumOffs);
        });
        rowsByLane = counts;
        return payloads;
    }

    private async Task<int> CopyPayloadsParallelAsync(
        string tableName, IntentStageTable table,
        int rowCount, int groups, byte[][] payloads, int[] rowsByLane, long sortMs,
        CopyTransactionCounts copyTransactions, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task[groups];
        // One value per COPY lane, including pool/semaphore wait and commit.
        // Reuse packing counts; do not parse the row payload again for diagnostics.
        var elapsedMsByLane = new double[groups];
        for (int g = 0; g < groups; g++)
        {
            int group = g;
            tasks[g] = Task.Run(async () =>
            {
                var payload = payloads[group];
                if (payload.Length == 0) return;
                long laneStarted = System.Diagnostics.Stopwatch.GetTimestamp();

                // Claim a slot in the plan's COPY share before renting from the pool:
                // overlapping phases share one budget, so a group waits on a cancellable
                // semaphore instead of timing out on a pool rent and failing the batch.
                using (var waitDiagnostic = MeasureApplyPhase(
                    "copy-lane-wait", tableName, lane: group, rows: rowsByLane[group]))
                {
                    await CopyConnectionBudget.WaitAsync(ct).ConfigureAwait(false);
                    waitDiagnostic?.Complete();
                }
                try
                {
                using var setupDiagnostic = MeasureApplyPhase(
                    "copy-lane-setup", tableName, lane: group, rows: rowsByLane[group]);
                await using var conn = await _ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                Interlocked.Increment(ref copyTransactions.Started);
                // The epoch advance rides the GUC batch in the same round trip; the
                // command has no positional parameters (Npgsql forbids $n in
                // multi-statement commands). nextval runs before the COPY below; each
                // parallel sub-transaction is its own write transaction and advances
                // once. The route flag was resolved on the control connection first.
                bool epochBump = Volatile.Read(ref _applyWriteEpochRoute) == 1;
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText =
                        TransactionGucs(Durability)
                        + (epochBump ? SqlCatalog.Get("write.advance_epoch").Text : "");
                    await guc.ExecuteNonQueryAsync(ct);
                }
                if (epochBump) Interlocked.Increment(ref _epochOwnBumpsSinceBaseline);
                string cols = IntentStage.CopyColumnList(table);
                var stream = await conn.BeginRawBinaryCopyAsync(
                    $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
                // Disposing the stream completes the COPY and reads its acknowledgement,
                // even when writing throws; the transaction and budget slot are released
                // by their own enclosing scopes.
                try
                {
                    setupDiagnostic?.Complete();
                    using var writeDiagnostic = MeasureApplyPhase(
                        "copy-lane-write", tableName, lane: group, rows: rowsByLane[group]);
                    await CopyTupleParser.WritePackedAsync(stream, payload, ct);
                    writeDiagnostic?.Complete();
                }
                finally
                {
                    using var finishDiagnostic = MeasureApplyPhase(
                        "copy-lane-finish", tableName, lane: group, rows: rowsByLane[group]);
                    await stream.DisposeAsync();
                    finishDiagnostic?.Complete();
                }
                using (var commitDiagnostic = MeasureApplyPhase(
                    "copy-lane-commit", tableName, lane: group, rows: rowsByLane[group]))
                {
                    await tx.CommitAsync(ct);
                    commitDiagnostic?.Complete();
                }
                Interlocked.Increment(ref copyTransactions.Committed);
                }
                finally
                {
                    // The connection and transaction are disposed on the way out of the
                    // try block, so the slot is released only after the pool has the
                    // connection back.
                    CopyConnectionBudget.Release();
                    elapsedMsByLane[group] =
                        System.Diagnostics.Stopwatch.GetElapsedTime(laneStarted).TotalMilliseconds;
                }
            }, ct);
        }
        await Task.WhenAll(tasks);
        sw.Stop();
        _log.LogInformation(
            "WS_APPLY copy {Table}: {Rows:N0} rows across {Groups} id-range connection(s) in {Ms:N0}ms ({Rps:N0} rows/s; sort {SortMs:N0}ms; lane rows {RowsByLane}; lane bytes {BytesByLane}; lane elapsed_ms {ElapsedMsByLane})",
            tableName, rowCount, groups, sw.ElapsedMilliseconds,
            rowCount / Math.Max(1e-3, sw.Elapsed.TotalSeconds), sortMs,
            string.Join(",", rowsByLane),
            string.Join(",", payloads.Select(static payload => payload.LongLength)),
            string.Join(",", elapsedMsByLane.Select(static value =>
                value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
        return 1;
    }

    private async Task<int> CopyPhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept,
        CopyTransactionCounts copyTransactions, CancellationToken ct)
    {
        if (kept.Count == 0) return 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long payloadBytes = TotalKeptBytes(kept);
        int groups = ResolveCopyGroups(kept.Count, payloadBytes);
        // Bounds-check StagedRowRef.Blob before packing: an IndexOutOfRange inside
        // BuildSortedCopyPayloads / PackFiltered carries no table name on the stack
        // (Release builds inline the packer), so the check names it here.
        int blobCount = blobs.Count;
        int minBlob = int.MaxValue, maxBlob = int.MinValue;
        for (int i = 0; i < kept.Count; i++)
        {
            int b = kept[i].Row.Blob;
            if (b < minBlob) minBlob = b;
            if (b > maxBlob) maxBlob = b;
            if ((uint)b >= (uint)blobCount)
                throw new InvalidOperationException(
                    $"COPY pack {tableName}: kept[{i}].Row.Blob={b} outside blobs[0..{blobCount}) "
                    + $"(kept={kept.Count:N0}, groups={groups}, patch={kept[i].Patch}, "
                    + $"len={kept[i].Row.Length}, off={kept[i].Row.Offset})");
        }
        byte[][] payloads;
        int[] rowsByLane;
        try
        {
            payloads = BuildSortedCopyPayloads(blobs, kept, groups, _leafModulus, out rowsByLane);
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"COPY pack {tableName}: IndexOutOfRange in BuildSortedCopyPayloads "
                + $"(kept={kept.Count:N0}, groups={groups}, blobs={blobCount}, "
                + $"blobIndexRange=[{minBlob}..{maxBlob}], payloadBytes={payloadBytes})",
                ex);
        }
        long sortMs = sw.ElapsedMilliseconds;
        return await CopyPayloadsParallelAsync(
            tableName, table, kept.Count, groups, payloads, rowsByLane, sortMs, copyTransactions, ct);
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets,
        long[]? patchedSums, IReadOnlyList<int>? sumValueOffsets, CancellationToken ct)
    {
        // Pack before opening the stream. PackFiltered can throw (its payload size
        // ceiling is the usual case); with the COPY already open, disposing the stream
        // would make the server report 22P04 "COPY file signature not recognized",
        // replacing the real exception. Packing first lets the real error propagate.
        byte[] packed = CopyTupleParser.PackFiltered(
            blobs, rows, patchedCounts, countValueOffsets, patchedSums, sumValueOffsets);
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await CopyTupleParser.WritePackedAsync(stream, packed, ct);
    }
}
