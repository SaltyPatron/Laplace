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

        // Entity sort+pack overlaps verification. The control transaction takes no
        // apply lock (IngestMutexGateTests.ContentAddressedApply_DoesNotAcquireGlobalAdvisoryLock).
        int workerConnectionLimit = ResolveWorkerConnectionLimit(_ds);
        bool singleConnectionApply = workerConnectionLimit <= 0;
        Task<(byte[][] Payloads, int[] RowsByLane, int Groups)>? optimisticEntCopy = null;
        long firstEntBytes = TotalEntityBytes(ents, firstEntIdx);
        int optimisticGroups = singleConnectionApply
            ? 1
            : ResolveCopyGroups(firstEntIdx.Count, firstEntBytes, workerConnectionLimit);
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
                await using var epoch = NpgsqlCatalog.Command(conn, tx, "write.advance_epoch");
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
                await using var journal = NpgsqlCatalog.Command(conn, tx, "write.journal_receipt_probe", token.ToBytes());
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

            // Probes fan out across pooled connections. No apply lock is held, so a
            // concurrent applier can commit an id after its probe says absent; the COPY
            // of that id then fails with 23505 and the caller re-probes the whole set
            // (TransientErrorRetryPolicy.ConcurrencyRetry), which subtracts what landed.
            using var verificationDiagnostic = MeasureApplyPhase("presence-verification");
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();

            // Empty-relation skip: if the physicalities or attestations relation has no
            // rows, every staged id for it is absent and the bitmap probe is not run.
            // Entity presence always goes through the id bitmap. A row a concurrent
            // applier commits after the emptiness check surfaces as 23505 on COPY and is
            // resolved by the same re-probe as any other lost race.
            //
            // These probes run on pooled connections, not the control transaction, so
            // each releases its AccessShare locks when it completes.
            long physEmptySkip = 0, attEmptySkip = 0;
            // Canonical entity storage is HASH(id). Tier is altitude, not
            // identity, and cannot participate in row presence or partition routing.
            var probeEntIdsUse = new List<Hash128>(firstEntIdx.Count);
            for (int k = 0; k < firstEntIdx.Count; k++)
                probeEntIdsUse.Add(ents.Ids[firstEntIdx[k]]);

            if (probePhysIdsUse.Count > 0)
            {
                rtProbe++;
                bool hasPhysRows = singleConnectionApply
                    ? await RelationHasRowsAsync(conn, tx, "physicalities", ct)
                    : await RelationHasRowsAsync(_ds, "physicalities", ct);
                if (!hasPhysRows)
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
                bool hasAttRows = singleConnectionApply
                    ? await RelationHasRowsAsync(conn, tx, "attestations", ct)
                    : await RelationHasRowsAsync(_ds, "attestations", ct);
                if (!hasAttRows)
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

            HashSet<Hash128> presentEntities;
            HashSet<Hash128> presentPhys;
            HashSet<Hash128> presentAtts;
            if (singleConnectionApply)
            {
                presentEntities = await ProbePresentControlAsync(
                    conn, tx, "write.entities_stored_bitmap", probeEntIdsUse,
                    static (_, _, _) => { }, r => rtProbe += r, ct).ConfigureAwait(false);
                presentPhys = await ProbePresentControlAsync(
                    conn, tx, "write.physicalities_exist_bitmap", probePhysIdsUse,
                    static (_, _, _) => { }, r => rtProbe += r, ct).ConfigureAwait(false);
                presentAtts = await ProbePresentKeyedControlAsync(
                    conn, tx, "write.attestations_exist_bitmap", probeAttIdsUse, probeAttTypesUse,
                    probeAttSubjectsUse, r => rtProbe += r, ct).ConfigureAwait(false);
            }
            else
            {
                var entProbeTask = ProbePresentCoreAsync(
                    "write.entities_stored_bitmap", probeEntIdsUse,
                    static (_, _, _) => { },
                    r => Interlocked.Add(ref rtProbe, r), ct);
                // Id-only physicality probe: physicalities are partitioned by HASH(id),
                // so a Hilbert-keyed probe would search the wrong partition and report a
                // stored row absent.
                var physProbeTask = ProbePresentCoreAsync(
                    "write.physicalities_exist_bitmap", probePhysIdsUse,
                    static (_, _, _) => { },
                    r => Interlocked.Add(ref rtProbe, r), ct);
                var attProbeTask = ProbePresentKeyedParallelAsync(
                    "write.attestations_exist_bitmap", probeAttIdsUse, probeAttTypesUse,
                    probeAttSubjectsUse, r => Interlocked.Add(ref rtProbe, r), ct);

                await Task.WhenAll(entProbeTask, physProbeTask, attProbeTask).ConfigureAwait(false);
                presentEntities = await entProbeTask.ConfigureAwait(false);
                presentPhys = await physProbeTask.ConfigureAwait(false);
                presentAtts = await attProbeTask.ConfigureAwait(false);
            }
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

            bool parallelCopy = !singleConnectionApply
                && ApplyParallelism > 1
                && (ResolveCopyGroups(keptEntCount, keptEntBytes, workerConnectionLimit) > 1
                    || ResolveCopyGroups(keptPhys.Count, keptPhysBytes, workerConnectionLimit) > 1
                    || ResolveCopyGroups(keptAtts.Count, keptAttBytes, workerConnectionLimit) > 1);

            if (!parallelCopy)
            {
                // Small applies stay fully atomic inside the control transaction.
                if (keptEnts.Count > 0 || keptPhys.Count > 0 || keptAtts.Count > 0)
                    copyTransactions.StartControl();
                if (keptEnts.Count > 0)
                {
                    SortKeptByLeafAndOrder(keptEnts, _leafModulus);
                    await CopyKeptAsync(conn, "entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, 0, keptEnts.Count, ct);
                    eIns = keptEnts.Count;
                    rtCopy++;
                }
                if (keptPhys.Count > 0)
                {
                    SortKeptByLeafAndOrder(keptPhys, _leafModulus);
                    await CopyKeptAsync(conn, "physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, 0, keptPhys.Count, ct);
                    pIns = keptPhys.Count;
                    rtCopy++;
                }
                if (keptAtts.Count > 0)
                {
                    SortKeptByLeafAndOrder(keptAtts, _leafModulus);
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
                // make referenced rows durable before their referencers. No lock keeps
                // other appliers out of the window: one that lands the same id first makes
                // this COPY fail with 23505, and the caller's re-probe subtracts it. A crash
                // mid-phase leaves no flush-journal receipt, and the retry's verification
                // subtracts what already landed.

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
                        SortKeptByLeafAndOrder(keptAtts, _leafModulus);
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
                await using var last = NpgsqlCatalog.Command(conn, null, "write.current_epoch");
                _epochAfterLastCommit = (long)(await last.ExecuteScalarAsync(ct))!;
                Interlocked.Exchange(ref _epochOwnBumpsSinceBaseline, 0);
                rtEpoch++;
            }
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (Exception failure)
            {
                _log.LogWarning(failure, "Working-set control transaction rollback failed; retaining the primary failure.");
            }
            throw;
        }
        int rt = rtLock + rtJournal + rtEpoch + rtProbe + rtCopy + rtMerge;
        _log.LogInformation(
            "WS_APPLY round-trips: {Total} = {Lock} lock + {Journal} journal + {Epoch} epoch + {Probe} probe + {Copy} copy + {Merge} merge "
            + "({E:N0}e/{P:N0}p/{A:N0}a novel, {Fold:N0} merged)",
            rt, rtLock, rtJournal, rtEpoch, rtProbe, rtCopy, rtMerge, eIns, pIns, aIns, aFold);
        return (eIns, pIns, aIns, aFold, eSkip, pSkip, rt, false, commit, copyTransactions);
    }
}
