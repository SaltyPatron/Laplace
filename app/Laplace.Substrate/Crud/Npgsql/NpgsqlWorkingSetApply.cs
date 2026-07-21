using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The Rule #8 write protocol (docs/specs/06_Engineering_Ruleset.txt): the
/// client already knows exactly what is novel (descent + hot caches decided
/// that before we got here), so the server's only remaining jobs are (1) a
/// bulk in-transaction verification of the claimed-novel ids — the guard
/// against a concurrent ingest having committed an overlapping subtree
/// between our unlocked descent and this transaction — and (2) pure COPY of
/// what survives, in entities → physicalities → attestations order. No temp
/// tables, no anti-join, no ON CONFLICT.
///
/// The verification probe is flat over the whole claimed-novel set, not
/// frontier-only: a concurrent ingest commits subtrees rooted at ITS roots,
/// which can sit strictly below our novel frontier (we hold novel sentence
/// S ⊃ word w; the other run committed w standalone — probing only S would
/// miss w and hit a PK violation).
///
/// Entity presence is checked with entities_stored_bitmap (perfcache fast
/// path OFF): this probe decides what gets written, and tier-0 codepoint
/// rows only exist because the unicode seed writes them through this lane —
/// answering their presence axiomatically would drop them from the write
/// list forever. The one licensed shortcut is DB-state-conditioned, not
/// axiomatic: once the UnicodeDecomposer L0 layer-complete marker exists in
/// the TARGET database (checked once per bulk run), the tier-0 entity space
/// is closed (UCD law: UnicodeDecomposer is the single origin of tier 0) and
/// every tier-0 id is present by definition — those ids skip the probe
/// client-side. During the unicode seed itself the marker is absent and
/// every tier-0 row still flows through the probe + COPY lane.
///
/// Attestation presence has a structural fast path: an attestation id is
/// BLAKE3(subject‖type‖object‖source‖context), so a row whose subject,
/// object, or context entity is NOVEL in this same batch cannot exist
/// server-side (entities always finish COPY before attestations start, and
/// the apply advisory lock serializes writers) — it skips the probe and goes
/// straight to COPY. Only attestations whose id-embedded entities all
/// already exist can be present, and only those are probed.
/// </summary>
public sealed partial class NpgsqlSubstrateWriter
{
    private const int ProbeChunkIds = 131_072;

    /// <summary>
    /// Rows below this stay on the fully-atomic single-transaction path;
    /// above it, COPY fans out across connections (per-table barriers keep
    /// entities durable before physicalities before attestations).
    /// </summary>
    private const int ParallelCopyMinRows = 65_536;

    internal static readonly int ApplyParallelism = CpuTopology.ResolveApplyPartitions();

    /// <summary>
    /// Run-scoped index cycle, active between BeginBulkRunAsync and
    /// CompleteBulkRunAsync. While active, qualifying applies drop
    /// secondaries but do NOT rebuild them — the rebuild happens once at
    /// run end. Only the apply lane touches this (applies are serialized
    /// by the runner and by the apply advisory lock), so no locking.
    /// </summary>
    private NpgsqlIndexCycle? _runCycle;

    /// <summary>
    /// Run-scoped persisted-id caches for the existence probe, active on the
    /// same BeginBulkRunAsync/CompleteBulkRunAsync bracket as <see cref="_runCycle"/>.
    /// Inside a bulk run applies are serialized (the runner and the apply advisory
    /// lock) and the substrate is append-only, so any content id THIS run has already
    /// COPYed-and-committed is durably present for the rest of the run — a later
    /// working set that re-stages it (low-tier codepoints/words recur in every working
    /// set) needs no server round-trip to learn it exists: the write lane treats it as
    /// present-and-skip, byte-for-byte what a probe hit would have produced. This does
    /// NOT weaken the pure-COPY invariant (the probe still guards concurrent overlaps
    /// for every id NOT known-persisted); it only removes re-probes of ids we ourselves
    /// wrote.
    ///
    /// EXACT sets, never a bloom: a false positive would treat a genuinely novel row as
    /// present and DROP it, so only a no-false-positive membership test may gate the
    /// skip. Bounded by DISTINCT persisted content (tens of millions of entities/
    /// physicalities on a full seed — a few GB, not the 12M×N re-probe volume), and
    /// cleared at run end. Attestations are deliberately NOT cached: a re-seen present
    /// attestation must still MERGE its observation count (its round-trip is not saved),
    /// and its id space is unbounded (billions on a model ingest).
    /// </summary>
    private HashSet<Hash128>? _persistedEntityIds;
    private HashSet<Hash128>? _persistedPhysIds;

    /// <summary>
    /// Tier-0 completeness gate, resolved ONCE per bulk run: true iff the
    /// UnicodeDecomposer L0 HasLayerCompleted marker exists in the target DB.
    /// While true, every tier-0 entity id is present by definition (the t0
    /// space is closed and fully seeded — UCD single-origin law) and skips
    /// the presence probe client-side. Conservative by construction: absent
    /// marker (fresh DB, mid-unicode-seed) leaves the gate off and every t0
    /// row probes as before. Entities only — t0 physicalities are NOT
    /// guaranteed 1:1 (projections land after identity content).
    /// </summary>
    private bool _tier0LayerComplete;

    public async Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        // Recover any journaled drops a crashed prior run left behind
        // BEFORE this run makes its own cycling decisions.
        await NpgsqlIndexCycle.RecoverAsync(_ds, _log, ct);
        _runCycle = new NpgsqlIndexCycle(_ds, _log);
        _persistedEntityIds = new HashSet<Hash128>();
        _persistedPhysIds = new HashSet<Hash128>();
        _tier0LayerComplete = await QueryTier0LayerCompleteAsync(ct);
        if (_tier0LayerComplete)
            _log.LogInformation(
                "WS_APPLY tier-0 gate ON: unicode L0 layer-complete marker present — "
                + "tier-0 entity ids answer presence client-side, zero probes");
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        var cycle = _runCycle;
        _runCycle = null;
        _persistedEntityIds = null;
        _persistedPhysIds = null;
        _tier0LayerComplete = false;
        if (cycle is not null)
            await cycle.FinishAsync(ct);
    }

    private async Task<bool> QueryTier0LayerCompleteAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT laplace.evidence_count("
            + "p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/0/v1'), "
            + "p_source => laplace.source_id('UnicodeDecomposer')) > 0";
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// Applies one whole working set in a single serialized transaction,
    /// claiming an idempotency token in laplace.ingest_flush_journal keyed
    /// by the change's intent hash. A retry after commit-ambiguity finds the
    /// token and returns a no-op instead of double-applying the additive
    /// attestation merges.
    /// </summary>
    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyManyInternalAsync(new[] { change }, change.Metadata.IntentId, ct);
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return ApplyManyInternalAsync(changes, workingSetToken: null, ct);
        return ApplyManyInternalAsync(changes, WorkingSetToken(changes), ct);
    }

    private static Hash128 WorkingSetToken(IReadOnlyList<SubstrateChange> changes)
    {
        var buf = new byte[changes.Count * 16];
        for (int i = 0; i < changes.Count; i++)
            changes[i].Metadata.IntentId.WriteBytes(buf.AsSpan(i * 16, 16));
        return Hash128.Blake3(buf);
    }

    private async Task<(int e, int p, int a, long fold, long eSkip, long pSkip, int rt, bool journalHit)>
        ApplyStagesCoreAsync(IReadOnlyList<IntentStage> stages, Hash128? workingSetToken, CancellationToken ct)
    {
        var entBlobs = CollectBlobs(stages, IntentStageTable.Entities, 4, "entities");
        var physBlobs = CollectBlobs(stages, IntentStageTable.Physicalities, 10, "physicalities");
        var attBlobs = CollectBlobs(stages, IntentStageTable.Attestations, 10, "attestations");

        var ents = CopyTupleParser.ParseEntities(entBlobs);
        var phys = CopyTupleParser.ParsePhysicalities(physBlobs);
        var atts = CopyTupleParser.ParseAttestations(attBlobs);

        // Distinct ids in first-seen order. Physicalities are verified by
        // their OWN content-addressed id, never inferred from their entity:
        // a physicality legitimately arrives for an already-stored entity
        // (projections and building blocks land after identity content).
        var entityIdSet = new HashSet<Hash128>(ents.Ids.Count);
        var probeEntityIds = new List<Hash128>(ents.Ids.Count);
        // Partition keys parallel to the probed ids (entities: LIST(tier);
        // physicalities: RANGE(hilbert_index)) — id alone cannot prune, so
        // an id-only probe pays one index descent per leaf.
        var probeEntityTiers = new List<short>(ents.Ids.Count);
        // Tier-0 gate (snapshot once per apply): with the unicode L0 layer
        // complete in the target DB, a tier-0 id is present by definition —
        // it never enters the probe and folds straight into the present set.
        bool tier0Gate = _tier0LayerComplete;
        List<Hash128>? tier0Present = tier0Gate ? new List<Hash128>() : null;
        for (int i = 0; i < ents.Ids.Count; i++)
            if (entityIdSet.Add(ents.Ids[i]))
            {
                if (tier0Gate && ents.Tiers[i] == 0)
                {
                    tier0Present!.Add(ents.Ids[i]);
                    continue;
                }
                probeEntityIds.Add(ents.Ids[i]);
                probeEntityTiers.Add(ents.Tiers[i]);
            }
        int distinctStagedEntities = entityIdSet.Count;

        var physIdSet = new HashSet<Hash128>(phys.Ids.Count);
        var probePhysIds = new List<Hash128>(phys.Ids.Count);
        var probePhysHilberts = new List<Hash128>(phys.Ids.Count);
        for (int i = 0; i < phys.Ids.Count; i++)
            if (physIdSet.Add(phys.Ids[i]))
            {
                probePhysIds.Add(phys.Ids[i]);
                probePhysHilberts.Add(phys.HilbertKeys[i]);
            }

        // Attestation duplicate collapse, exactly apply_batch's semantics:
        // representative = latest-ts staged row, observation counts sum.
        var attGroups = new Dictionary<Hash128, (int RepIdx, long MaxTs, long Games)>(atts.Ids.Count);
        // The keyed attestation probe needs the partition keys parallel to
        // the probed ids: id alone cannot prune LIST(type_id)->HASH(subject).
        // The first-occurrence source index rides along so the structural
        // novelty filter below can read each candidate's object/context ids.
        var probeAttIds = new List<Hash128>(atts.Ids.Count);
        var probeAttTypes = new List<Hash128>(atts.Ids.Count);
        var probeAttSubjects = new List<Hash128>(atts.Ids.Count);
        var probeAttSrcIdx = new List<int>(atts.Ids.Count);
        for (int i = 0; i < atts.Ids.Count; i++)
        {
            if (attGroups.TryGetValue(atts.Ids[i], out var g))
            {
                long games = AttestationMergeMath.SafeAddGames(g.Games, atts.Counts[i]);
                attGroups[atts.Ids[i]] = atts.TimestampsPgUs[i] > g.MaxTs
                    ? (i, atts.TimestampsPgUs[i], games)
                    : (g.RepIdx, g.MaxTs, games);
            }
            else
            {
                attGroups[atts.Ids[i]] = (i, atts.TimestampsPgUs[i], atts.Counts[i]);
                probeAttIds.Add(atts.Ids[i]);
                probeAttTypes.Add(atts.TypeIds[i]);
                probeAttSubjects.Add(atts.SubjectIds[i]);
                probeAttSrcIdx.Add(i);
            }
        }

        // Per-phase round-trip counters — summed into the returned total AND logged as a
        // breakdown, so the operator sees WHERE the round-trips go (lock / journal / probe /
        // copy / merge) instead of one opaque number. Probe fans across connections → atomic.
        int rtLock = 0, rtJournal = 0, rtProbe = 0, rtCopy = 0, rtMerge = 0;
        int eIns = 0, pIns = 0, aIns = 0;
        long aFold = 0, eSkip = 0, pSkip = 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        // Bulk-apply session SEMANTICS only (FK-trigger bypass, relaxed durability for
        // this bulk tx, no JIT for COPY). Magnitude tuning — work_mem,
        // maintenance_work_mem, parallel workers — is owned by tune-pg.cmd (derived from
        // Cpu/MemoryTopology) and INHERITED here, never re-set with a hardcoded literal.
        await using var tx = await AdvisoryTxLock.BeginWithLockAsync(
            conn, "laplace_apply_batch",
            "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = off; "
            + "SET LOCAL jit = off; ",
            _log, ct);
        try
        {
            rtLock++;

            if (workingSetToken is Hash128 token)
            {
                await using var journal = conn.CreateCommand();
                journal.Transaction = tx;
                journal.CommandText =
                    "INSERT INTO laplace.ingest_flush_journal (working_set_id) "
                    + "VALUES ($1) ON CONFLICT (working_set_id) DO NOTHING";
                journal.Parameters.Add(new NpgsqlParameter
                { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                int claimed = await journal.ExecuteNonQueryAsync(ct);
                rtJournal++;
                if (claimed == 0)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    _log.LogInformation(
                        "WORKING_SET_REPLAY token={Token} already journaled — skipping apply",
                        token);
                    return (0, 0, 0, 0, 0, 0, rtLock + rtJournal, true);
                }
            }

            // Run-persisted-id fast path: an id THIS run already COPYed-and-committed is
            // durably present (append-only substrate + serialized applies), so it needs
            // no probe — drop it from the probe input and fold it straight into the
            // present set. Everything NOT known-persisted is still probed, so the
            // concurrent-overlap guard behind the pure-COPY invariant is untouched.
            // Snapshot the caches once: null outside a bulk run (standalone applies always
            // probe in full — the safe default).
            var persistedEnt = _persistedEntityIds;
            var persistedPhys = _persistedPhysIds;
            var probeEntIdsUse = probeEntityIds;
            var probeEntTiersUse = probeEntityTiers;
            long entCacheSkip = 0;
            if (persistedEnt is { Count: > 0 })
            {
                probeEntIdsUse = new List<Hash128>(probeEntityIds.Count);
                probeEntTiersUse = new List<short>(probeEntityIds.Count);
                for (int i = 0; i < probeEntityIds.Count; i++)
                    if (persistedEnt.Contains(probeEntityIds[i])) entCacheSkip++;
                    else { probeEntIdsUse.Add(probeEntityIds[i]); probeEntTiersUse.Add(probeEntityTiers[i]); }
            }
            var probePhysIdsUse = probePhysIds;
            var probePhysHilbertsUse = probePhysHilberts;
            long physCacheSkip = 0;
            if (persistedPhys is { Count: > 0 })
            {
                probePhysIdsUse = new List<Hash128>(probePhysIds.Count);
                probePhysHilbertsUse = new List<Hash128>(probePhysIds.Count);
                for (int i = 0; i < probePhysIds.Count; i++)
                    if (persistedPhys.Contains(probePhysIds[i])) physCacheSkip++;
                    else { probePhysIdsUse.Add(probePhysIds[i]); probePhysHilbertsUse.Add(probePhysHilberts[i]); }
            }

            // Probes fan out across pooled connections. Correct under the
            // held advisory lock: every snapshot starts after the lock was
            // acquired, so anything a prior applier committed is visible.
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();

            // I/O locality — the load-bearing fix for large-DB probes. The native existence
            // bitmaps do keyed lookups into the PARTITIONED tables (entities LIST(tier),
            // physicalities RANGE(hilbert), attestations LIST(type_id)->HASH(subject)). Probing
            // in staged (content-hash-random) order scatters each 131k chunk across every
            // partition leaf and heap page — fine while the table fits cache, catastrophic once
            // it doesn't (MEASURED on Wiktionary: a single verify grew to 37-53 min of cache-cold
            // RANDOM I/O, worsening as the DB grew). Sorting each probe by its partition key makes
            // every chunk a CONTIGUOUS partition range = sequential index+heap scan. The probes
            // return a present-id SET, so input order is semantically irrelevant — this reorders
            // I/O only. The permutation is applied identically to every parallel array, so keyed
            // alignment is preserved by construction (guarded downstream anyway).
            if (probeEntIdsUse.Count > 1)
            {
                var perm = BuildProbePermutation(probeEntIdsUse.Count, (a, b) =>
                {
                    int c = probeEntTiersUse[a].CompareTo(probeEntTiersUse[b]);
                    return c != 0 ? c : probeEntIdsUse[a].CompareToBytewise(probeEntIdsUse[b]);
                });
                probeEntIdsUse = ApplyProbePermutation(probeEntIdsUse, perm);
                probeEntTiersUse = ApplyProbePermutation(probeEntTiersUse, perm);
            }
            if (probePhysIdsUse.Count > 1)
            {
                var perm = BuildProbePermutation(probePhysIdsUse.Count,
                    (a, b) => probePhysHilbertsUse[a].CompareToBytewise(probePhysHilbertsUse[b]));
                probePhysIdsUse = ApplyProbePermutation(probePhysIdsUse, perm);
                probePhysHilbertsUse = ApplyProbePermutation(probePhysHilbertsUse, perm);
            }
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
            var entProbeTask = ProbePresentTieredParallelAsync(
                "laplace.entities_stored_bitmap", probeEntIdsUse, probeEntTiersUse,
                r => Interlocked.Add(ref rtProbe, r), ct);
            var physProbeTask = ProbePresentPairKeyedParallelAsync(
                "laplace.physicalities_exist_bitmap", probePhysIdsUse, probePhysHilbertsUse,
                r => Interlocked.Add(ref rtProbe, r), ct);

            var presentEntities = await entProbeTask;
            // Fold the known-persisted ids (excluded from the probe above) back into the
            // present set — the write lane below skips a row iff its id is present, and
            // these are present by our own committed writes. Tier-0 gated ids
            // are present by the layer-complete marker.
            if (persistedEnt is { Count: > 0 })
                foreach (var id in probeEntityIds)
                    if (persistedEnt.Contains(id)) presentEntities.Add(id);
            if (tier0Present is not null)
                foreach (var id in tier0Present) presentEntities.Add(id);


            long attStructuralSkip = 0;
            var probeAttIdsUse = probeAttIds;
            var probeAttTypesUse = probeAttTypes;
            var probeAttSubjectsUse = probeAttSubjects;
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

            var presentAtts = await ProbePresentKeyedParallelAsync(
                "laplace.attestations_exist_bitmap", probeAttIdsUse, probeAttTypesUse,
                probeAttSubjectsUse, r => Interlocked.Add(ref rtProbe, r), ct);
            var presentPhys = await physProbeTask;
            if (persistedPhys is { Count: > 0 })
                foreach (var id in probePhysIds)
                    if (persistedPhys.Contains(id)) presentPhys.Add(id);
            _log.LogInformation(
                "WS_APPLY verify: {Entities:N0}e+{Phys:N0}p+{Atts:N0}a ids probed in {Ms:N0}ms "
                + "(skipped {ECache:N0}e/{PCache:N0}p cached, {T0:N0}e tier0-gate, {AStruct:N0}a novel-by-construction; "
                + "present: {PresentE:N0}e/{PresentP:N0}p/{PresentA:N0}a)",
                probeEntIdsUse.Count, probePhysIdsUse.Count, probeAttIdsUse.Count, phaseSw.ElapsedMilliseconds,
                entCacheSkip, physCacheSkip, tier0Present?.Count ?? 0, attStructuralSkip,
                presentEntities.Count, presentPhys.Count, presentAtts.Count);

            // Entities: first occurrence of each id, minus stored rows.
            // Kept rows carry their id so parallel COPY groups can own
            // DISJOINT btree key ranges — content-addressed ids are
            // uniformly random, and un-partitioned parallel inserts
            // measured as LWLock:BufferContent pile-ups on shared index
            // pages. Range-partitioned sorted groups fill leaves like a
            // parallel bulk index build instead.
            var keptEnts = new List<KeptRow>(ents.Rows.Count);
            var seenEnt = new HashSet<Hash128>(distinctStagedEntities);
            // Novel ids to fold into the run-persisted cache — ONLY after this apply
            // commits (below). null outside a bulk run: no cache, nothing to collect.
            var novelEntIds = persistedEnt is null ? null : new List<Hash128>(keptEnts.Capacity);
            for (int i = 0; i < ents.Ids.Count; i++)
            {
                if (!seenEnt.Add(ents.Ids[i])) continue;
                if (presentEntities.Contains(ents.Ids[i])) { eSkip++; continue; }
                keptEnts.Add(new KeptRow(ents.Ids[i], ents.Rows[i], -1, 0));
                novelEntIds?.Add(ents.Ids[i]);
            }

            // Physicalities: first occurrence of each id, minus stored rows.
            // Sort key = HILBERT INDEX, not id: the contended index here is
            // the coord GiST, and hilbert order is its spatial locality —
            // range-partitioned groups land in disjoint GiST subtrees the
            // way id-sorted groups land in disjoint btree leaf ranges.
            var keptPhys = new List<KeptRow>(phys.Rows.Count);
            var seenPhys = new HashSet<Hash128>(phys.Ids.Count);
            // Physicality KeptRow.SortKey is the hilbert key (GiST locality), not the id,
            // so novel ids are collected here explicitly rather than recovered post-sort.
            var novelPhysIds = persistedPhys is null ? null : new List<Hash128>(keptPhys.Capacity);
            for (int i = 0; i < phys.Ids.Count; i++)
            {
                if (!seenPhys.Add(phys.Ids[i])) continue;
                if (presentPhys.Contains(phys.Ids[i])) { pSkip++; continue; }
                keptPhys.Add(new KeptRow(phys.HilbertKeys[i], phys.Rows[i], -1, 0));
                novelPhysIds?.Add(phys.Ids[i]);
            }

            // Attestations: novel groups COPY their representative (count
            // patched to the group sum when duplicates collapsed); present
            // groups merge via one UPDATE.
            var novelRepIdx = new List<int>(attGroups.Count);
            // Merge rows carry their PARTITION KEYS (type, subject): the
            // routed attestation_merge prunes per relation type to that
            // type's hash leaves and seeks the leaf PK — the bare-id UPDATE
            // it replaces Append-scanned every attestation leaf per chunk
            // (~10s/chunk flat, the OMW 9-minute merge).
            var mergeRows = new List<(Hash128 Type, Hash128 Subj, Hash128 Id, long Games, DateTime Ts)>();
            foreach (var (id, g) in attGroups)
            {
                if (presentAtts.Contains(id))
                {
                    mergeRows.Add((atts.TypeIds[g.RepIdx], atts.SubjectIds[g.RepIdx], id,
                        g.Games, AttestationMergeMath.TimestampFromPgMicros(g.MaxTs)));
                }
                else
                {
                    novelRepIdx.Add(g.RepIdx);
                }
            }
            novelRepIdx.Sort();
            var keptAtts = new List<KeptRow>(novelRepIdx.Count);
            for (int k = 0; k < novelRepIdx.Count; k++)
            {
                int i = novelRepIdx[k];
                long games = attGroups[atts.Ids[i]].Games;
                keptAtts.Add(new KeptRow(
                    atts.Ids[i], atts.Rows[i],
                    games == atts.Counts[i] ? -1 : games,
                    atts.CountValueOffsets[i]));
            }

            bool parallelCopy = ApplyParallelism > 1
                && keptEnts.Count + keptPhys.Count + keptAtts.Count >= ParallelCopyMinRows;

            if (!parallelCopy)
            {
                // Small applies stay fully atomic inside the control tx.
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
                // Bulk COPY fans out across connections owning DISJOINT
                // index keyspaces (sorted + range-partitioned: id for btree
                // tables, hilbert for the coord GiST). Fresh-seed-shaped
                // volumes additionally cycle secondary indexes: drop → COPY
                // clean heaps → parallel sort-based rebuilds (journal-backed
                // for crash recovery). Per-table barriers keep referenced
                // rows durable before their referencers (entities →
                // physicalities → attestations). The control tx holds the
                // advisory lock across the whole window, so no other applier
                // interleaves; a crash mid-phase leaves no flush-journal
                // token and the replay's verification subtracts whatever
                // landed.
                //
                // Cycle scope: inside a bulk run the run-scoped cycle owns
                // the indexes — each qualifying apply drops whatever is
                // still standing (idempotent: dropped indexes no longer
                // appear in pg_index) and the ONE rebuild happens at
                // CompleteBulkRunAsync. Outside a bulk run (no bracket),
                // the apply cycles locally as before. Correct with the
                // indexes down between applies: every write-lane presence
                // probe (*_stored_bitmap / *_present_ordinals) is a PK
                // lookup, and PK/unique/exclusion never cycle.
                var cycle = _runCycle;
                if (cycle is null)
                {
                    cycle = new NpgsqlIndexCycle(_ds, _log);
                    await NpgsqlIndexCycle.RecoverAsync(_ds, _log, ct);
                }
                await cycle.BeginAsync(new[]
                {
                    ("entities", (long)keptEnts.Count),
                    ("physicalities", (long)keptPhys.Count),
                    ("attestations", (long)keptAtts.Count),
                    // Consensus is written by the client fold, which has no cycle
                    // of its own and paid 6 live secondary-index inserts per novel
                    // row (fold collapsed to ~5K rel/s on the big sources). Drop
                    // them in the same run-scoped bracket, rebuilt once at run end;
                    // the fold's prior-read is a PK lookup, unaffected by dropping
                    // the secondaries. Staged proxied by the attestation count.
                    ("consensus", (long)keptAtts.Count),
                }, ct);

                // Entities COMPLETE first — the structural attestation
                // novelty rule (and crash recovery) depends on "attestation
                // committed ⇒ its batch's entities committed". Physicalities
                // and attestations have no cross-dependency and are the two
                // expensive phases: they overlap, so the phase cost is
                // max(phys, atts) instead of the old sequential sum.
                rtCopy += await CopyPhaseParallelAsync("entities", IntentStageTable.Entities,
                    entBlobs, keptEnts, ct);
                eIns = keptEnts.Count;
                var physCopyTask = CopyPhaseParallelAsync("physicalities", IntentStageTable.Physicalities,
                    physBlobs, keptPhys, ct);
                var attCopyTask = CopyPhaseParallelAsync("attestations", IntentStageTable.Attestations,
                    attBlobs, keptAtts, ct);
                await Task.WhenAll(physCopyTask, attCopyTask);
                rtCopy += physCopyTask.Result + attCopyTask.Result;
                pIns = keptPhys.Count;
                aIns = keptAtts.Count;

                if (!ReferenceEquals(cycle, _runCycle))
                    await cycle.FinishAsync(ct);
            }

            if (mergeRows.Count > 0)
            {
                var mergeSw = System.Diagnostics.Stopwatch.StartNew();
                // Routed merge: sorted by (type, subject, id) so the server
                // function's per-type loop reads contiguous slices and every
                // writer acquires row locks in one global order; chunked
                // because unbounded unnest over large bytea[] arrays AVs
                // postgres 18.
                mergeRows.Sort(static (a, b) =>
                {
                    int c = a.Type.CompareToBytewise(b.Type);
                    if (c != 0) return c;
                    c = a.Subj.CompareToBytewise(b.Subj);
                    return c != 0 ? c : a.Id.CompareToBytewise(b.Id);
                });
                // PARALLEL by relation type (2026-07-21). This was a serial
                // for-loop on the apply's single connection — the only phase of
                // the apply that never fanned out, while entities/physicalities/
                // attestations all COPY across ApplyParallelism connections.
                // MEASURED on the OMW seed: 3,495,027 present rows = 107 serial
                // chunks, ~9 minutes, and it is what the run sat on before being
                // cancelled (the phase log never printed because it never
                // finished).
                //
                // Types partition the work SAFELY: attestations is
                // LIST(type_id) -> HASH(subject_id), so two groups holding
                // disjoint type sets touch disjoint leaves and can never
                // contend on the same row, index page, or partition lock. The
                // (type, subject, id) sort is preserved inside each group, so
                // row-lock acquisition stays ordered within a partition and the
                // cross-applier advisory lock still serializes whole applies.
                // Same connection-per-group shape as CopyPhaseParallelAsync —
                // the apply is already multi-transaction there, so this
                // introduces no new atomicity boundary.
                const int mergeChunk = 32_768;
                // CHUNK-level distribution, not whole-type bins (2026-07-21).
                // The first version packed whole relation types into bins so a
                // type could never be split across connections. Relation volume
                // is heavily skewed — measured mid-OMW, the top consensus types
                // are 995,176 / 711,861 / 686,409 rows — so the largest type
                // swallowed a bin and ran ALONE as the tail while every other
                // connection sat idle. Sampled live: 23 of 25 probes of
                // pg_stat_activity showed exactly ONE active backend, and it was
                // always attestation_merge. Type-granular packing cannot
                // parallelize a skewed batch; it only parallelizes a balanced one.
                //
                // Splitting a type across connections is SAFE: mergeRows is
                // deduplicated by attestation id, so distinct chunks hold
                // disjoint ROWS and no two connections can contend on the same
                // tuple. Partition-level locks are RowExclusiveLock, which is
                // self-compatible, and the cross-applier advisory lock still
                // serializes whole applies. The (type, subject, id) sort is
                // retained so each chunk stays partition-contiguous — a chunk
                // that straddles a type boundary is fine, attestation_merge
                // already loops the distinct types it is handed.
                var chunks = new List<(int Off, int Len)>();
                for (int off = 0; off < mergeRows.Count; off += mergeChunk)
                    chunks.Add((off, Math.Min(mergeChunk, mergeRows.Count - off)));
                int mergeGroups = (int)Math.Min(ApplyParallelism, Math.Max(1, chunks.Count));
                var bins = new List<(int Off, int Len)>[mergeGroups];
                for (int g = 0; g < mergeGroups; g++) bins[g] = new List<(int, int)>();
                for (int c = 0; c < chunks.Count; c++) bins[c % mergeGroups].Add(chunks[c]);

                long mergeFolded = 0;
                int mergeRt = 0;
                await CpuTopology.RunPinnedAsyncParallel(mergeGroups, async (g, token) =>
                {
                    if (bins[g].Count == 0) return;
                    await using var mconn = await _ds.OpenConnectionAsync(token);
                    await using var mtx = await mconn.BeginTransactionAsync(token);
                    await using (var guc = mconn.CreateCommand())
                    {
                        guc.Transaction = mtx;
                        guc.CommandText = "SET LOCAL synchronous_commit = off; SET LOCAL jit = off";
                        await guc.ExecuteNonQueryAsync(token);
                    }
                    foreach (var (spanOff, spanLen) in bins[g])
                        for (int off = spanOff; off < spanOff + spanLen; off += mergeChunk)
                        {
                            int m = Math.Min(mergeChunk, spanOff + spanLen - off);
                            var ids = new byte[m][];
                            var types = new byte[m][];
                            var subjects = new byte[m][];
                            var games = new long[m];
                            var ts = new DateTime[m];
                            for (int i = 0; i < m; i++)
                            {
                                var r = mergeRows[off + i];
                                ids[i] = r.Id.ToBytes();
                                types[i] = r.Type.ToBytes();
                                subjects[i] = r.Subj.ToBytes();
                                games[i] = r.Games;
                                ts[i] = r.Ts;
                            }
                            await using var merge = mconn.CreateCommand();
                            merge.Transaction = mtx;
                            merge.CommandTimeout = 0;
                            merge.CommandText = "SELECT laplace.attestation_merge($1, $2, $3, $4, $5)";
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = ts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                            Interlocked.Add(ref mergeFolded,
                                (long)(await merge.ExecuteScalarAsync(token) ?? 0L));
                            Interlocked.Increment(ref mergeRt);
                        }
                    await mtx.CommitAsync(token);
                }, ct);
                aFold += mergeFolded;
                rtMerge += mergeRt;
                _log.LogInformation(
                    "WS_APPLY merge: {Rows:N0} present rows merged in {Ms:N0}ms ({Rps:N0} rows/s)",
                    mergeRows.Count, mergeSw.ElapsedMilliseconds,
                    mergeRows.Count / Math.Max(1e-3, mergeSw.Elapsed.TotalSeconds));
            }

            await tx.CommitAsync(ct);

            // ONLY now that the whole apply committed are these ids durably persisted, so
            // subsequent applies this run may skip re-probing them. Done post-commit so a
            // rolled-back apply never poisons the cache with never-persisted ids; a miss is
            // harmless — the next apply simply probes and finds them present. (The parallel
            // COPY sub-txns commit their rows independently, so a control-tx failure after
            // that point still leaves the rows present and a later probe will catch them —
            // the cache is a pure optimization, never a correctness input.)
            if (persistedEnt is not null && novelEntIds is not null)
                foreach (var id in novelEntIds) persistedEnt.Add(id);
            if (persistedPhys is not null && novelPhysIds is not null)
                foreach (var id in novelPhysIds) persistedPhys.Add(id);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }

        int rt = rtLock + rtJournal + rtProbe + rtCopy + rtMerge;
        _log.LogInformation(
            "WS_APPLY round-trips: {Total} = {Lock} lock + {Journal} journal + {Probe} probe + {Copy} copy + {Merge} merge "
            + "({E:N0}e/{P:N0}p/{A:N0}a novel, {Fold:N0} merged)",
            rt, rtLock, rtJournal, rtProbe, rtCopy, rtMerge, eIns, pIns, aIns, aFold);
        return (eIns, pIns, aIns, aFold, eSkip, pSkip, rt, false);
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
    /// Shared chunked, connection-parallel presence probe. Sends the ids in
    /// ProbeChunkIds-sized chunks as $1 (bytea[]), lets
    /// <paramref name="bindKeys"/> add the target table's partition-key
    /// arrays for the same [start, start+n) window, and decodes the returned
    /// bitmap back to hit ids. Every probe shape (tiered, pair-keyed,
    /// triple-keyed) rides this one implementation.
    /// </summary>
    private async Task<HashSet<Hash128>> ProbePresentCoreAsync(
        string commandText, IReadOnlyList<Hash128> ids,
        Action<NpgsqlParameterCollection, int, int> bindKeys,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;

        int chunkCount = (ids.Count + ProbeChunkIds - 1) / ProbeChunkIds;
        var perChunk = new List<Hash128>[chunkCount];

        async Task ProbeChunkAsync(int c, CancellationToken token)
        {
            int start = c * ProbeChunkIds;
            int n = Math.Min(ProbeChunkIds, ids.Count - start);
            var chunk = new byte[n][];
            for (int i = 0; i < n; i++) chunk[i] = ids[start + i].ToBytes();

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = commandText;
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            bindKeys(cmd.Parameters, start, n);
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
            int workers = Math.Min(chunkCount, Math.Min(ApplyParallelism, 8));
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

    /// <summary>Tier-keyed presence probe (entities: LIST(tier), t2 further
    /// HASH(id)). The write lane stages every entity's tier, so the probe
    /// prunes to one index descent per id instead of one per leaf.</summary>
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

    /// <summary>Pair-keyed presence probe (physicalities:
    /// RANGE(hilbert_index)). The write lane stages every row's hilbert key
    /// (it is the parallel-COPY sort key already), so the probe prunes
    /// per row instead of descending every leaf per id.</summary>
    private Task<HashSet<Hash128>> ProbePresentPairKeyedParallelAsync(
        string function, IReadOnlyList<Hash128> ids, IReadOnlyList<Hash128> keys,
        Action<int> addRoundTrips, CancellationToken ct)
    {
        if (keys.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {keys.Count} keys");
        return ProbePresentCoreAsync($"SELECT {function}($1, $2)", ids,
            (parameters, start, n) =>
            {
                var chunk = new byte[n][];
                for (int i = 0; i < n; i++) chunk[i] = keys[start + i].ToBytes();
                parameters.Add(new NpgsqlParameter
                { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            }, addRoundTrips, ct);
    }

    /// <summary>Triple-keyed presence probe (attestations: LIST(type_id) ->
    /// HASH(subject_id); an id-only probe pays one index descent per leaf —
    /// ~145x).</summary>
    // Identity permutation sorted by a partition-key comparison over indices, so all parallel
    // probe arrays can be reordered together for sequential-I/O locality (see the call site).
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

    /// <summary>SortKey partitions parallel COPY groups into disjoint index
    /// keyspaces: the row id for btree-indexed tables, the hilbert index for
    /// physicalities (coord GiST locality).</summary>
    private readonly record struct KeptRow(Hash128 SortKey, StagedRowRef Row, long Patch, int CountOff);

    private static async Task CopyKeptAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        var rows = new List<StagedRowRef>(count);
        long[]? patches = null;
        int[]? countOffs = null;
        bool anyPatch = false;
        for (int i = start; i < start + count; i++)
            if (kept[i].Patch >= 0) { anyPatch = true; break; }
        if (anyPatch)
        {
            patches = new long[count];
            countOffs = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            var k = kept[start + i];
            rows.Add(k.Row);
            if (patches is not null)
            {
                patches[i] = k.Patch;
                countOffs![i] = k.CountOff;
            }
        }
        await CopyFilteredAsync(conn, tableName, table, blobs, rows, patches, countOffs, ct);
    }

    private async Task<int> CopyPhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, CancellationToken ct)
    {
        if (kept.Count == 0) return 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int groups = (int)Math.Min(ApplyParallelism, Math.Max(1L, kept.Count / 16_384));

        // Sort by id so range-partitioned groups own disjoint btree
        // keyspaces — measured LWLock:BufferContent contention disappears
        // when concurrent COPY backends never share index pages.
        kept.Sort(static (a, b) => a.SortKey.CompareToBytewise(b.SortKey));
        int per = (kept.Count + groups - 1) / groups;

        await CpuTopology.RunPinnedAsyncParallel(groups, async (g, token) =>
        {
            int start = g * per;
            if (start >= kept.Count) return;
            int n = Math.Min(per, kept.Count - start);

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var tx = await conn.BeginTransactionAsync(token);
            await using (var guc = conn.CreateCommand())
            {
                guc.Transaction = tx;
                guc.CommandText =
                    "SET LOCAL session_replication_role = replica; "
                    + "SET LOCAL synchronous_commit = off; "
                    + "SET LOCAL jit = off";
                await guc.ExecuteNonQueryAsync(token);
            }
            await CopyKeptAsync(conn, tableName, table, blobs, kept, start, n, token);
            await tx.CommitAsync(token);
        }, ct);

        sw.Stop();
        _log.LogInformation(
            "WS_APPLY copy {Table}: {Rows:N0} rows across {Groups} id-range connection(s) in {Ms:N0}ms ({Rps:N0} rows/s)",
            tableName, kept.Count, groups, sw.ElapsedMilliseconds,
            kept.Count / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
        return groups;
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets, CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await CopyTupleParser.WriteFilteredAsync(
            stream, blobs, rows, patchedCounts, countValueOffsets, ct);
    }
}
