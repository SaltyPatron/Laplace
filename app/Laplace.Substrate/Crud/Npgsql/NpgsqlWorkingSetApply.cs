using System.Runtime.InteropServices;
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
/// Attestation presence is always verified. The former structural-novelty
/// shortcut inferred that an attestation embedding a novel-looking entity
/// could not already exist; live OMW ingest disproved that premise by millions
/// of rows and COPY failed on 23505. The detailed evidence remains beside the
/// attestation probe below.
/// </summary>
public sealed partial class NpgsqlSubstrateWriter
{
    // Was 131_072: a 70–120k-id Wiktionary verify became chunkCount=1 (serial) while
    // IngestSizing already advertised probe_chunk=16384 for the compose lane. Match
    // that grain so ApplyParallelism actually fans the bitmap probes.
    private const int ProbeChunkIds = 16_384;

    /// <summary>
    /// Rows below this stay on the fully-atomic single-transaction path;
    /// above it, COPY fans out across connections (per-table barriers keep
    /// entities durable before physicalities before attestations).
    /// </summary>
    private const int ParallelCopyMinRows = 65_536;
    /// <summary>
    /// Skip run-cache / ladder ledger fills above this. The fill is dictionary adds
    /// (~sub-second per million ids) against multi-second applies; the old 100k gate
    /// silently excluded every Wiktionary working set (738k+ distinct entities), so
    /// the ledger stayed EMPTY for the whole 2026-08-06 full-file run while its
    /// staging-site cost was still paid per surface. The gate exists to bound the
    /// post-COPY tax on pathological id floods, not to starve normal corpora.
    /// </summary>
    private const int MaxRunCacheFillIds = 2_000_000;

    internal static readonly int ApplyParallelism = CpuTopology.ResolveApplyPartitions();

    /// <summary>
    /// Run-scoped index cycle, active between BeginBulkRunAsync and
    /// CompleteBulkRunAsync. While active, qualifying applies drop
    /// secondaries but do NOT rebuild them — the rebuild happens once at
    /// run end. Only the apply lane touches this (applies are serialized
    /// by the runner and by the apply advisory lock), so no locking.
    /// </summary>
    private NpgsqlIndexCycle? _runCycle;

    // Cumulative staged rows across the whole bulk-run bracket. BeginAsync's
    // volume gates (MinRowsToCycle, CycleMinLiveFraction) answer "is this RUN
    // fresh-seed shaped?" — but each apply stages only tens of thousands of
    // rows, so testing per-apply counts meant the run-scoped cycle could never
    // fire mid-run no matter how large the run grew (measured 2026-08-12: the
    // UD/OMW seed paid live secondary maintenance for every row — 21.3KB WAL
    // per consensus insert across 9 indexes vs 7.5KB with them cycled). The
    // drop decision is idempotent (each qualifying apply drops whatever still
    // stands), so passing running totals lets it fire the moment cumulative
    // volume crosses the gates.
    private long _runStagedEnts, _runStagedPhys, _runStagedAtts;

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
    // ConcurrentDictionary: presence-complete applies skip the advisory lock and
    // may run in parallel; HashSet is not safe for that.
    private System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _persistedEntityIds;
    private System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _persistedPhysIds;
    /// <summary>In-flight COPY claims (parallel apply). Not durable presence.</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _claimedEntityIds;
    private System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _claimedPhysIds;
    private System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _claimedAttIds;

    /// <summary>
    /// When true, the matching run cache holds EVERY id in the target at run
    /// start (plus ids this run COPYed). A miss is then definitive absence —
    /// the bitmap probe is skipped. Incomplete caches (the default) may only
    /// treat hits as present; misses still probe.
    /// Entities+physicalities only — attestation preload is banned in-band
    /// (measured ~429s for 85M ids). Campaign prep: e/p outside the timed window.
    /// </summary>
    private bool _entityPresenceComplete;
    private bool _physPresenceComplete;

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
        _runStagedEnts = _runStagedPhys = _runStagedAtts = 0;
        _persistedEntityIds = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>();
        _persistedPhysIds = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>();
        _claimedEntityIds = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>();
        _claimedPhysIds = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>();
        _claimedAttIds = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>();
        _entityPresenceComplete = false;
        _physPresenceComplete = false;
        // Content roots proven present feed the spine's pre-derivation ladder skip.
        Laplace.Decomposers.Abstractions.ContentLadderLedger.Begin();
        _tier0LayerComplete = await QueryTier0LayerCompleteAsync(ct);
        if (_tier0LayerComplete)
            _log.LogInformation(
                "WS_APPLY tier-0 gate ON: unicode L0 layer-complete marker present — "
                + "tier-0 entity ids answer presence client-side, zero probes");
        // Campaign bulk loads (index secondaries deferred) preload e+p only.
        // Attestation preload (~429s / 85M) is banned in-band.
        if (EnvFlag.IsSet("LAPLACE_PRESENCE_PRELOAD")
            || NpgsqlIndexCycle.Deferred)
            await PreloadPresenceSetsAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        var cycle = _runCycle;
        _runCycle = null;
        _runStagedEnts = _runStagedPhys = _runStagedAtts = 0;
        _persistedEntityIds = null;
        _persistedPhysIds = null;
        _claimedEntityIds = null;
        _claimedPhysIds = null;
        _claimedAttIds = null;
        _entityPresenceComplete = false;
        _physPresenceComplete = false;
        Laplace.Decomposers.Abstractions.ContentLadderLedger.End();
        _tier0LayerComplete = false;
        if (cycle is not null)
            await cycle.FinishAsync(ct);
    }

    /// <summary>
    /// Load every entity + physicality id into the run caches so a miss means
    /// absent. Attestations are NOT preloaded (85M ids measured ~429s in-band).
    /// </summary>
    private async Task PreloadPresenceSetsAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eTask = LoadRelationIdsBinaryAsync(_persistedEntityIds!, "entities", ct);
        var pTask = LoadRelationIdsBinaryAsync(_persistedPhysIds!, "physicalities", ct);
        await Task.WhenAll(eTask, pTask).ConfigureAwait(false);
        _entityPresenceComplete = true;
        _physPresenceComplete = true;
        _log.LogInformation(
            "WS_APPLY presence preload: {E:N0}e+{P:N0}p in {Ms:N0}ms — e/p bitmap probes skipped; att still probes",
            _persistedEntityIds!.Count, _persistedPhysIds!.Count,
            sw.ElapsedMilliseconds);
    }

    private async Task LoadRelationIdsBinaryAsync(
        System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte> into,
        string table, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        // Parent partitioned tables reject COPY tablename TO — use a SELECT query.
        await using var exporter = await conn.BeginBinaryExportAsync(
            $"COPY (SELECT id FROM laplace.{table}) TO STDOUT (FORMAT BINARY)", ct)
            .ConfigureAwait(false);
        while (await exporter.StartRowAsync(ct).ConfigureAwait(false) >= 0)
        {
            var raw = await exporter.ReadAsync<byte[]>(NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
            if (raw is { Length: >= 16 })
                into.TryAdd(Hash128.FromBytes(raw), 0);
        }
    }

    private async Task<bool> QueryTier0LayerCompleteAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ops.evidence_count("
            + "p_type => realize.canonical_id('substrate/type/HasLayerCompleted/0/v1'), "
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
        var prepSw = System.Diagnostics.Stopwatch.StartNew();
        var entBlobs = CollectBlobs(stages, IntentStageTable.Entities, 4, "entities");
        var physBlobs = CollectBlobs(stages, IntentStageTable.Physicalities, 10, "physicalities");
        var attBlobs = CollectBlobs(stages, IntentStageTable.Attestations, 12, "attestations");
        long blobMs = prepSw.ElapsedMilliseconds;

        var ents = CopyTupleParser.ParseEntities(entBlobs);
        var phys = CopyTupleParser.ParsePhysicalities(physBlobs);
        var atts = CopyTupleParser.ParseAttestations(attBlobs);
        long parseMs = prepSw.ElapsedMilliseconds;

        // Distinct entity ids in first-seen order across EVERY staged intent.
        // A builder/content bank deduplicates only its own stage; IngestRunner
        // combines many changes into one working-set apply (Unicode's failed
        // seed combined 627), so uniqueness never crosses this boundary unless
        // the shared writer enforces it here. COPY has no ON CONFLICT: one
        // duplicate in the claimed-novel set aborts the whole working set.
        //
        // Identity is the content id; tier is metadata, not part of identity,
        // matching SubstrateChangeBuilder and the managed-stage aggregation.
        // Preserve the first row exactly as those paths do.
        //
        // Physicalities are verified by
        // their OWN content-addressed id, never inferred from their entity:
        // a physicality legitimately arrives for an already-stored entity
        // (projections and building blocks land after identity content).
        // First-occurrence row indices only — probe id/tier lists are built
        // AFTER invert if any ids still need the bitmap path. MEASURED: copying
        // 500k ids into probe lists was ~110ms of the prep dedupe bucket.
        // Tier-0 gate (snapshot once per apply): with the unicode L0 layer
        // complete in the target DB, a tier-0 id is present by definition —
        // it never enters the probe and folds straight into the present set.
        bool tier0Gate = _tier0LayerComplete;
        var dedupeSw = System.Diagnostics.Stopwatch.StartNew();
        var firstEntIdx = DistinctEntityRowIndices(ents, tier0Gate, out var tier0Present);
        int distinctStagedEntities = firstEntIdx.Count + (tier0Present?.Count ?? 0);
        long entDedupeMs = dedupeSw.ElapsedMilliseconds;
        // Materialized only when invert leaves a bitmap remainder.
        List<Hash128> probeEntityIds = new();
        List<short> probeEntityTiers = new();

        var physIdSet = new HashSet<Hash128>(phys.Ids.Count);
        var probePhysIds = new List<Hash128>(phys.Ids.Count);
        for (int i = 0; i < phys.Ids.Count; i++)
            if (physIdSet.Add(phys.Ids[i]))
                probePhysIds.Add(phys.Ids[i]);

        // Attestation duplicate collapse, exactly apply_batch's semantics:
        // representative = latest-ts staged row, observation counts sum, and
        // sum_score_fp1e9 sums with them — the persisted evidence stays the
        // exact record of what the group folded.
        var attGroups = new Dictionary<Hash128, (int RepIdx, long MaxTs, long Games, long Sum)>(atts.Ids.Count);
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
                long sum = AttestationMergeMath.SafeAddScores(g.Sum, atts.SumScores[i]);
                attGroups[atts.Ids[i]] = atts.TimestampsPgUs[i] > g.MaxTs
                    ? (i, atts.TimestampsPgUs[i], games, sum)
                    : (g.RepIdx, g.MaxTs, games, sum);
            }
            else
            {
                attGroups[atts.Ids[i]] = (i, atts.TimestampsPgUs[i], atts.Counts[i], atts.SumScores[i]);
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

        long prepMs = prepSw.ElapsedMilliseconds;
        _log.LogInformation(
            "WS_APPLY prep: {Ms:N0}ms (blobs {BlobMs:N0} + parse {ParseMs:N0} + ent-dedupe {EntDedupeMs:N0} + rest {RestMs:N0}; {E:N0}e→{EDistinct:N0} distinct/{P:N0}p/{A:N0}a)",
            prepMs, blobMs, parseMs - blobMs, entDedupeMs, prepMs - parseMs - entDedupeMs,
            ents.Ids.Count, distinctStagedEntities, phys.Ids.Count, atts.Ids.Count);

        // Entity sort+pack before advisory lock / verify — overlaps
        // open+lock+invert. Contended pack-during-parse was a net loss.
        Task<(byte[][] Payloads, int Groups)>? optimisticEntCopy = null;
        if (firstEntIdx.Count >= ParallelCopyMinRows && phys.Ids.Count == 0 && atts.Ids.Count == 0)
        {
            var idxSnap = firstEntIdx;
            var entsSnap = ents;
            var blobsSnap = entBlobs;
            int groupsSnap = ResolveCopyGroups(idxSnap.Count, sharedSecondaryKeys: 1);
            optimisticEntCopy = Task.Run(() =>
            {
                var payloads = BuildSortedEntityPayloads(
                    blobsSnap, entsSnap, idxSnap, groupsSnap);
                return (payloads, groupsSnap);
            }, ct);
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        // Bulk-apply session SEMANTICS only (FK-trigger bypass, relaxed durability for
        // this bulk tx, no JIT for COPY). Magnitude tuning — work_mem,
        // maintenance_work_mem, parallel workers — is owned by tune-pg.cmd (derived from
        // Cpu/MemoryTopology) and INHERITED here, never re-set with a hardcoded literal.
        //
        // Presence sets make novelty probes cheap, but they do not coordinate with a
        // second process. The advisory transaction lock remains the cross-process
        // apply mutex and supplies the bounded lock-timeout diagnostics.
        const string ApplyGucs =
            "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = off; "
            + "SET LOCAL jit = off; ";
        NpgsqlTransaction tx = await AdvisoryTxLock.BeginWithLockAsync(
            conn, "laplace_apply_batch", ApplyGucs, _log, ct);
        await using (tx)
        {
        // Ids this apply successfully claimed — released after commit, or on failure so
        // peers are not wedged and do not double-COPY after a partial parallel COPY.
        var claimedEntThis = new List<Hash128>();
        var claimedPhysThis = new List<Hash128>();
        var claimedAttThis = new List<Hash128>();
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
            // Working set of first-occurrence indices still needing verify.
            var entVerifyIdx = firstEntIdx;
            long entCacheSkip = 0;
            if (persistedEnt is { Count: > 0 } && entVerifyIdx.Count > 0)
            {
                var kept = new List<int>(entVerifyIdx.Count);
                for (int k = 0; k < entVerifyIdx.Count; k++)
                {
                    int i = entVerifyIdx[k];
                    if (persistedEnt.ContainsKey(ents.Ids[i])) entCacheSkip++;
                    else kept.Add(i);
                }
                entVerifyIdx = kept;
            }
            var probePhysIdsUse = probePhysIds;
            long physCacheSkip = 0;
            if (persistedPhys is { Count: > 0 })
            {
                probePhysIdsUse = new List<Hash128>(probePhysIds.Count);
                for (int i = 0; i < probePhysIds.Count; i++)
                    if (persistedPhys.ContainsKey(probePhysIds[i])) physCacheSkip++;
                    else probePhysIdsUse.Add(probePhysIds[i]);
            }

            // Probes fan out across pooled connections. Correct under the
            // held advisory lock: every snapshot starts after the lock was
            // acquired, so anything a prior applier committed is visible.
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();

            // Empty-partition probe skip (under the apply advisory lock only).
            // If a LIST(tier) leaf / whole phys|att heap has zero rows, every
            // staged id for that keyspace is absent — the bitmap probe would
            // return an all-zero mask after paying full chunk round-trips.
            // Exact EXISTS under the lock; not reltuples. Does NOT weaken the
            // pure-COPY invariant: non-empty partitions still probe in full.
            long entEmptySkip = 0, physEmptySkip = 0, attEmptySkip = 0;
            long entInvertResolved = 0;
            var presentFromInvert = new HashSet<Hash128>();
            List<Hash128> probeEntIdsUse = probeEntityIds;
            List<short> probeEntTiersUse = probeEntityTiers;

            // Complete presence sets: cache skip already removed every present
            // id; the remainder is novel by exact membership — no invert/bitmap.
            if (_entityPresenceComplete)
            {
                probeEntIdsUse = new List<Hash128>();
                probeEntTiersUse = new List<short>();
            }
            else if (entVerifyIdx.Count > 0)
            {
                var tierSample = new List<short>(entVerifyIdx.Count);
                for (int k = 0; k < entVerifyIdx.Count; k++)
                    tierSample.Add(ents.Tiers[entVerifyIdx[k]]);
                var nonemptyTiers = await NonEmptyEntityTiersAsync(conn, tx, tierSample, ct);
                rtProbe++; // one EXISTS roster round-trip
                if (nonemptyTiers.Count < DistinctShortCount(tierSample))
                {
                    var kept = new List<int>(entVerifyIdx.Count);
                    for (int k = 0; k < entVerifyIdx.Count; k++)
                    {
                        int i = entVerifyIdx[k];
                        if (nonemptyTiers.Contains(ents.Tiers[i])) kept.Add(i);
                        else entEmptySkip++;
                    }
                    entVerifyIdx = kept;
                }

                // Smaller-build-side verify (under the same lock). Presence is still
                // proven before COPY. Per LIST(tier) leaf: count committed rows; if
                // that set is strictly smaller than the staged probe for the tier,
                // load present ids and test locally (classic join build-side choice).
                // If the leaf is larger, keep the bitmap probe. No fixed numeric dial —
                // only which side is smaller.
                if (entVerifyIdx.Count > 0)
                {
                    var inverted = await InvertEntityTiersBySmallerSideAsync(
                        conn, tx, ents, entVerifyIdx, presentFromInvert, ct);
                    rtProbe += inverted.RoundTrips;
                    entInvertResolved = inverted.Resolved;
                    probeEntIdsUse = new List<Hash128>(inverted.RemainingIdx.Count);
                    probeEntTiersUse = new List<short>(inverted.RemainingIdx.Count);
                    for (int k = 0; k < inverted.RemainingIdx.Count; k++)
                    {
                        int i = inverted.RemainingIdx[k];
                        probeEntIdsUse.Add(ents.Ids[i]);
                        probeEntTiersUse.Add(ents.Tiers[i]);
                    }
                }
            }

            if (_physPresenceComplete)
            {
                // Misses already filtered out of probePhysIdsUse above; remainder novel.
                probePhysIdsUse = new List<Hash128>();
            }
            else if (probePhysIdsUse.Count > 0)
            {
                rtProbe++;
                if (!await RelationHasRowsAsync(conn, tx, "physicalities", ct))
                {
                    physEmptySkip = probePhysIdsUse.Count;
                    probePhysIdsUse = new List<Hash128>();
                }
            }

            // I/O locality — the load-bearing fix for large-DB probes. The native existence
            // bitmaps do keyed lookups into the PARTITIONED tables (entities LIST(tier),
            // physicalities HASH(id), attestations LIST(type_id)->HASH(subject)). Probing
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
                // Sorted by ID since physicalities became HASH(id)/PK(id). Hash
                // routing is not monotonic in id, so a chunk is not one contiguous
                // partition — but within each of the 64 buckets the probed ids are
                // still ascending, so every bucket's PK index is walked forward
                // instead of randomly. Same property attestations gets from
                // HASH(subject_id) probed in subject order.
                var perm = BuildProbePermutation(probePhysIdsUse.Count,
                    (a, b) => probePhysIdsUse[a].CompareToBytewise(probePhysIdsUse[b]));
                probePhysIdsUse = ApplyProbePermutation(probePhysIdsUse, perm);
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
            // Attestation prep before the fan: empty-relation skip + sort. The three
            // bitmap probes then run concurrently — attestation no longer waits on
            // the entity result (novel-by-construction shortcut is gone; see comment
            // block above). Verify wall becomes max(ent,phys,att).
            long attStructuralSkip = 0;
            var probeAttIdsUse = probeAttIds;
            var probeAttTypesUse = probeAttTypes;
            var probeAttSubjectsUse = probeAttSubjects;
            if (probeAttIdsUse.Count > 0)
            {
                rtProbe++;
                if (!await RelationHasRowsAsync(conn, tx, "attestations", ct))
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

            var entProbeTask = ProbePresentTieredParallelAsync(
                "laplace.entities_stored_bitmap", probeEntIdsUse, probeEntTiersUse,
                r => Interlocked.Add(ref rtProbe, r), ct);
            // Id-only phys probe: hilbert-keyed routing hits the wrong HASH(id)
            // partition under the current schema (absent-for-stored is fatal).
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
            if (presentFromInvert.Count > 0)
                foreach (var id in presentFromInvert) presentEntities.Add(id);
            // Fold the known-persisted ids (excluded from the probe above) back into the
            // present set — the write lane below skips a row iff its id is present, and
            // these are present by our own committed writes. Tier-0 gated ids
            // are present by the layer-complete marker.
            if (persistedEnt is { Count: > 0 })
                for (int k = 0; k < firstEntIdx.Count; k++)
                {
                    var id = ents.Ids[firstEntIdx[k]];
                    if (persistedEnt.ContainsKey(id)) presentEntities.Add(id);
                }
            if (tier0Present is not null)
                foreach (var id in tier0Present) presentEntities.Add(id);
            if (persistedPhys is { Count: > 0 })
                foreach (var id in probePhysIds)
                    if (persistedPhys.ContainsKey(id)) presentPhys.Add(id);
            _log.LogInformation(
                "WS_APPLY verify: {Entities:N0}e+{Phys:N0}p+{Atts:N0}a ids probed in {Ms:N0}ms "
                + "(skipped {ECache:N0}e/{PCache:N0}p cached, {T0:N0}e tier0-gate, {EEmpty:N0}e/{PEmpty:N0}p/{AEmpty:N0}a empty-partition, {EInv:N0}e smaller-side invert, {AStruct:N0}a novel-by-construction; "
                + "present: {PresentE:N0}e/{PresentP:N0}p/{PresentA:N0}a)",
                probeEntIdsUse.Count, probePhysIdsUse.Count, probeAttIdsUse.Count, phaseSw.ElapsedMilliseconds,
                entCacheSkip, physCacheSkip, tier0Present?.Count ?? 0,
                entEmptySkip, physEmptySkip, attEmptySkip, entInvertResolved, attStructuralSkip,
                presentEntities.Count, presentPhys.Count, presentAtts.Count);

            // Entities: first occurrence of each id, minus stored rows.
            // Kept rows carry their id so parallel COPY groups can own
            // DISJOINT btree key ranges — content-addressed ids are
            // uniformly random, and un-partitioned parallel inserts
            // measured as LWLock:BufferContent pile-ups on shared index
            // pages. Range-partitioned sorted groups fill leaves like a
            // parallel bulk index build instead.
            List<KeptRow> keptEnts;
            byte[][]? prebuiltEntPayloads = null;
            int prebuiltEntGroups = 0;
            // Only allocate the run-cache novel list when we will fill it.
            var novelEntIds = persistedEnt is null || firstEntIdx.Count > MaxRunCacheFillIds
                ? null
                : new List<Hash128>(firstEntIdx.Count);
            var keptEntTypes = new HashSet<Hash128>();
            bool anyPresent = presentEntities.Count > 0;
            if (optimisticEntCopy is not null)
            {
                var prepared = await optimisticEntCopy.ConfigureAwait(false);
                if (!anyPresent)
                {
                    prebuiltEntPayloads = prepared.Payloads;
                    prebuiltEntGroups = prepared.Groups;
                    keptEnts = new List<KeptRow> { default };
                    if (novelEntIds is not null)
                    {
                        CollectionsMarshal.SetCount(novelEntIds, firstEntIdx.Count);
                        var ns = CollectionsMarshal.AsSpan(novelEntIds);
                        var ids = CollectionsMarshal.AsSpan(ents.Ids);
                        var idx = CollectionsMarshal.AsSpan(firstEntIdx);
                        for (int k = 0; k < ns.Length; k++) ns[k] = ids[idx[k]];
                    }
                    if (ents.TypeIds.Count > 0)
                        keptEntTypes.Add(ents.TypeIds[firstEntIdx[0]]);
                }
                else
                {
                    keptEnts = new List<KeptRow>(firstEntIdx.Count);
                    for (int k = 0; k < firstEntIdx.Count; k++)
                    {
                        int i = firstEntIdx[k];
                        var eid = ents.Ids[i];
                        if (presentEntities.Contains(eid) || (persistedEnt?.ContainsKey(eid) ?? false))
                        { eSkip++; continue; }
                        // Claim before COPY so a parallel apply cannot stage the same id.
                        if (_claimedEntityIds is not null && !_claimedEntityIds.TryAdd(eid, 0))
                        { eSkip++; presentEntities.Add(eid); continue; }
                        claimedEntThis.Add(eid);
                        keptEnts.Add(new KeptRow(
                            CopyPartitionKey.ForEntityId(eid),
                            CopyPartitionKey.ForEntityId(eid), ents.Rows[i], -1, 0));
                        novelEntIds?.Add(eid);
                        if (i < ents.TypeIds.Count) keptEntTypes.Add(ents.TypeIds[i]);
                    }
                    if (eSkip == 0)
                    {
                        prebuiltEntPayloads = prepared.Payloads;
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
                    if (presentEntities.Contains(eid) || (persistedEnt?.ContainsKey(eid) ?? false))
                    { eSkip++; continue; }
                    if (_claimedEntityIds is not null && !_claimedEntityIds.TryAdd(eid, 0))
                    { eSkip++; presentEntities.Add(eid); continue; }
                    claimedEntThis.Add(eid);
                    keptEnts.Add(new KeptRow(
                        CopyPartitionKey.ForEntityId(eid),
                        CopyPartitionKey.ForEntityId(eid), ents.Rows[i], -1, 0));
                    novelEntIds?.Add(eid);
                    if (i < ents.TypeIds.Count) keptEntTypes.Add(ents.TypeIds[i]);
                }
            }

            // Physicalities: first occurrence of each id, minus stored rows.
            // Sort key = ID, matching physicalities' HASH(id) partitioning and its
            // PK(id). It was the hilbert index while the table was RANGE(hilbert),
            // for coord-GiST spatial locality — but hilbert is a curve position,
            // not a hash, so uniform bands over a clustered distribution sent
            // 58.96% of the table (and of every batch) into one partition and
            // collapsed these 8 lanes to 1: 527 rows/s with one backend working
            // and 21 idle at COMMIT. Ids are content hashes, so id-range groups
            // are equal-sized by construction and land in disjoint PK leaf ranges.
            // The GiST gives up insert locality it was not being paid for: no
            // installed read prunes on hilbert, so no KNN ever used the bands.
            var keptPhys = new List<KeptRow>(phys.Rows.Count);
            var seenPhys = new HashSet<Hash128>(phys.Ids.Count);
            var novelPhysIds = _claimedPhysIds is null && persistedPhys is null
                ? null
                : new List<Hash128>(keptPhys.Capacity);
            for (int i = 0; i < phys.Ids.Count; i++)
            {
                if (!seenPhys.Add(phys.Ids[i])) continue;
                var pid = phys.Ids[i];
                if (presentPhys.Contains(pid) || (persistedPhys?.ContainsKey(pid) ?? false))
                { pSkip++; continue; }
                if (_claimedPhysIds is not null && !_claimedPhysIds.TryAdd(pid, 0))
                { pSkip++; presentPhys.Add(pid); continue; }
                claimedPhysThis.Add(pid);
                // Lane by id (uniform), ORDER by hilbert (coord GiST locality).
                keptPhys.Add(new KeptRow(
                    CopyPartitionKey.ForEntityId(pid),
                    CopyPartitionKey.ForHilbertIndex(phys.HilbertKeys[i]),
                    phys.Rows[i], -1, 0));
                novelPhysIds?.Add(pid);
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
            var mergeRows = new List<(Hash128 Type, Hash128 Subj, Hash128 Id, long Games, long Sum, DateTime Ts)>();
            foreach (var (id, g) in attGroups)
            {
                if (presentAtts.Contains(id))
                {
                    mergeRows.Add((atts.TypeIds[g.RepIdx], atts.SubjectIds[g.RepIdx], id,
                        g.Games, g.Sum, AttestationMergeMath.TimestampFromPgMicros(g.MaxTs)));
                }
                else if (_claimedAttIds is not null && !_claimedAttIds.TryAdd(id, 0))
                {
                    // Peer owns in-flight COPY. Wait for claim release (post-commit) —
                    // never merge against an uncommitted peer row (count corruption).
                    var waitSw = System.Diagnostics.Stopwatch.StartNew();
                    int claimDelayMs = 1;
                    while (_claimedAttIds.ContainsKey(id))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (waitSw.ElapsedMilliseconds >= 120_000)
                        {
                            throw new TimeoutException(
                                $"Attestation claim {id} remained held for 120 seconds; "
                                + "refusing to merge against a possibly uncommitted row.");
                        }
                        await Task.Delay(claimDelayMs, ct).ConfigureAwait(false);
                        claimDelayMs = Math.Min(claimDelayMs * 2, 100);
                    }
                    mergeRows.Add((atts.TypeIds[g.RepIdx], atts.SubjectIds[g.RepIdx], id,
                        g.Games, g.Sum, AttestationMergeMath.TimestampFromPgMicros(g.MaxTs)));
                }
                else
                {
                    claimedAttThis.Add(id);
                    novelRepIdx.Add(g.RepIdx);
                }
            }
            novelRepIdx.Sort();
            var keptAtts = new List<KeptRow>(novelRepIdx.Count);
            for (int k = 0; k < novelRepIdx.Count; k++)
            {
                int i = novelRepIdx[k];
                var group = attGroups[atts.Ids[i]];
                bool collapsed = group.Games != atts.Counts[i] || group.Sum != atts.SumScores[i];
                keptAtts.Add(new KeptRow(
                    CopyPartitionKey.ForEntityId(atts.Ids[i]),
                    CopyPartitionKey.ForEntityId(atts.Ids[i]), atts.Rows[i],
                    collapsed ? group.Games : -1,
                    atts.CountValueOffsets[i],
                    collapsed ? group.Sum : 0,
                    atts.SumScoreValueOffsets[i]));
            }

            int keptEntCount = prebuiltEntPayloads is not null
                ? firstEntIdx.Count - (int)eSkip
                : keptEnts.Count;
            _log.LogInformation(
                "WS_APPLY kept: {E:N0}e/{P:N0}p/{A:N0}a novel after verify in {Ms:N0}ms since verify-start",
                keptEntCount, keptPhys.Count, keptAtts.Count, phaseSw.ElapsedMilliseconds);

            bool parallelCopy = ApplyParallelism > 1
                && keptEntCount + keptPhys.Count + keptAtts.Count >= ParallelCopyMinRows;

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
                bool runScoped = cycle is not null;
                if (cycle is null)
                {
                    cycle = new NpgsqlIndexCycle(_ds, _log);
                    await NpgsqlIndexCycle.RecoverAsync(_ds, _log, ct);
                }
                // Inside a bulk run the volume gates see CUMULATIVE staged rows
                // (_runStagedEnts and siblings): the bracket owns the whole run,
                // so the fresh-seed-shaped question is about the run's volume,
                // not one apply's ~50k rows — per-apply counts could never cross
                // MinRowsToCycle and the bracket never fired (2026-08-12 seeds
                // paid live-index maintenance end to end). Outside a bracket the
                // local cycle keeps per-apply semantics unchanged.
                long entStaged = keptEnts.Count, physStaged = keptPhys.Count, attStaged = keptAtts.Count;
                if (runScoped)
                {
                    entStaged = Interlocked.Add(ref _runStagedEnts, entStaged);
                    physStaged = Interlocked.Add(ref _runStagedPhys, physStaged);
                    attStaged = Interlocked.Add(ref _runStagedAtts, attStaged);
                }
                await cycle.BeginAsync(new[]
                {
                    ("entities", entStaged),
                    ("physicalities", physStaged),
                    ("attestations", attStaged),
                    // Consensus is written by the client fold, which has no cycle
                    // of its own and paid 6 live secondary-index inserts per novel
                    // row (fold collapsed to ~5K rel/s on the big sources). Drop
                    // them in the same run-scoped bracket, rebuilt once at run end;
                    // the fold's prior-read is a PK lookup, unaffected by dropping
                    // the secondaries. Staged proxied by the attestation count.
                    ("consensus", attStaged),
                }, ct);

                // Entities COMPLETE first — the structural attestation
                // novelty rule (and crash recovery) depends on "attestation
                // committed ⇒ its batch's entities committed". Physicalities
                // and attestations have no cross-dependency and are the two
                // expensive phases: they overlap, so the phase cost is
                // max(phys, atts) instead of the old sequential sum.
                // Entity batches often share one type_id (throughput fixture; many
                // real sources too). Id-range parallelism then contends on the same
                // type/tier_type btree leaves — cap groups when the batch is
                // type-homogeneous so COPY is not fighting itself.
                if (prebuiltEntPayloads is not null)
                {
                    rtCopy += await CopyPayloadsParallelAsync(
                        "entities", IntentStageTable.Entities,
                        keptEntCount, prebuiltEntGroups, prebuiltEntPayloads, sortMs: 0, ct);
                }
                else
                {
                    rtCopy += await CopyPhaseParallelAsync("entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, ct, sharedSecondaryKeys: keptEntTypes.Count);
                }
                eIns = keptEntCount;
                var physCopyTask = CopyPhaseParallelAsync("physicalities", IntentStageTable.Physicalities,
                    physBlobs, keptPhys, ct, sharedSecondaryKeys: int.MaxValue);
                var attCopyTask = CopyPhaseParallelAsync("attestations", IntentStageTable.Attestations,
                    attBlobs, keptAtts, ct, sharedSecondaryKeys: int.MaxValue);
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
                        // enable_mergejoin/hashjoin off is not a hint, it is the shape of
                        // this statement. attestation_merge drives a BOUNDED array (<=
                        // mergeChunk rows) into a PRIMARY KEY — the nested loop is right
                        // at every size, and a plan that sorts or hashes the target
                        // relation never is.
                        //
                        // The UPDATE pins type_id (the LIST key) as a literal, but
                        // subject_id — the HASH key — arrives from the join, so hash
                        // pruning cannot happen at plan time and all 8 children stay in
                        // the plan. That leaves two plans within 20% of each other
                        // (Merge Append over the whole relation at 64,489 vs nested-loop
                        // PK probes at ~77,000), and the planner picks the O(relation)
                        // one as soon as the relation is big enough. MEASURED on the
                        // 2026-07-26 OMW seed, consecutive applies: 165,806 rows at
                        // 71,829 rows/s, then 242,563 rows at 156 rows/s — a 450x cliff
                        // crossed with no code change, purely from the table growing.
                        guc.CommandText = "SET LOCAL synchronous_commit = off; SET LOCAL jit = off; "
                            + "SET LOCAL enable_mergejoin = off; SET LOCAL enable_hashjoin = off";
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
                            var sums = new long[m];
                            var ts = new DateTime[m];
                            for (int i = 0; i < m; i++)
                            {
                                var r = mergeRows[off + i];
                                ids[i] = r.Id.ToBytes();
                                types[i] = r.Type.ToBytes();
                                subjects[i] = r.Subj.ToBytes();
                                games[i] = r.Games;
                                sums[i] = r.Sum;
                                ts[i] = r.Ts;
                            }
                            await using var merge = mconn.CreateCommand();
                            merge.Transaction = mtx;
                            merge.CommandTimeout = 0;
                            merge.CommandText = "SELECT consensus.attestation_merge($1, $2, $3, $4, $5, $6)";
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            merge.Parameters.Add(new NpgsqlParameter
                            { Value = sums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
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
            //
            // Fill the probe-skip cache with EVERY distinct entity/phys this working set
            // staged — not only the novel COPYed subset, and not gated on MaxRunCacheFillIds.
            // After commit, present-at-probe and newly-COPYed rows are equally durable under
            // append-only law. MEASURED 2026-08-04 ChessPgn (OTB year on a DB that already
            // holds another OTB year): first WS staged ~360k distinct entities, verify paid
            // ~34s with present≈346k, and because firstEntIdx.Count > MaxRunCacheFillIds the
            // old novel-only fill left novelEntIds=null → cache stayed empty → every later
            // WS re-probed the same shared position graph with "skipped 0e cached". HashSet
            // add of ~360k ids is ~100ms; repeating a 34s bitmap is the gate killer.
            // ContentLadderLedger stays novel/staged-gated below (provenance across sources).
            if (persistedEnt is not null && firstEntIdx.Count > 0)
            {
                var ids = CollectionsMarshal.AsSpan(ents.Ids);
                var idx = CollectionsMarshal.AsSpan(firstEntIdx);
                for (int k = 0; k < idx.Length; k++)
                    persistedEnt.TryAdd(ids[idx[k]], 0);
                if (tier0Present is not null)
                    foreach (var id in tier0Present) persistedEnt.TryAdd(id, 0);
            }
            if (persistedPhys is not null && probePhysIds.Count > 0)
            {
                for (int i = 0; i < probePhysIds.Count; i++)
                    persistedPhys.TryAdd(probePhysIds[i], 0);
            }
            // Release in-flight claims only after commit — peers may now merge safely.
            if (_claimedEntityIds is not null)
            {
                foreach (var id in claimedEntThis)
                    _claimedEntityIds.TryRemove(id, out _);
            }
            if (_claimedPhysIds is not null)
            {
                foreach (var id in claimedPhysThis)
                    _claimedPhysIds.TryRemove(id, out _);
            }
            if (_claimedAttIds is not null)
            {
                foreach (var id in claimedAttThis)
                    _claimedAttIds.TryRemove(id, out _);
            }

            // Same commit boundary, same reason: a root may only answer "ladder already
            // deposited" once it is durably in the target.
            //
            // The feed is what THIS APPLY STAGED (first-occurrence entity ids), not
            // everything found present. Presence alone would let one source's earlier
            // deposit suppress the next source's FIRST witnessing of the same surface —
            // WordNet minting "casa" would silence OMW's own attestation of it, and
            // provenance is never mashed.
            // The ledger is armed per bulk run, and a bulk run is one source, so a root
            // enters only after this source has staged it and that stage has committed.
            // What the skip then suppresses is strictly the 2nd..Nth re-emission within
            // the run — the batch-boundary artifact, nothing a source asserts.
            //
            // Ids withheld from the probe are consistent with that: cache-skipped ids are
            // already ledgered from the apply that committed them, and tier-0 gated ids
            // are single codepoints with no ladder below them to re-walk.
            // Same size gate as run-cache fill: ledger mark over 500k roots is
            // another post-COPY tax on the throughput path; large applies skip.
            if (novelEntIds is { Count: > 0 and <= MaxRunCacheFillIds })
                Laplace.Decomposers.Abstractions.ContentLadderLedger.MarkPersisted(novelEntIds);
            else if (novelEntIds is null && firstEntIdx.Count > 0
                     && firstEntIdx.Count <= MaxRunCacheFillIds)
            {
                var stagedIds = new Hash128[firstEntIdx.Count];
                var ids = CollectionsMarshal.AsSpan(ents.Ids);
                var idx = CollectionsMarshal.AsSpan(firstEntIdx);
                for (int k = 0; k < stagedIds.Length; k++) stagedIds[k] = ids[idx[k]];
                Laplace.Decomposers.Abstractions.ContentLadderLedger.MarkPersisted(stagedIds);
            }
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            // Parallel COPY may already have durably inserted some claimed ids.
            // Always drop the claims so peers merge (or a retry probes present)
            // instead of waiting 120s on a zombie claim / double-COPY (23505).
            if (_claimedEntityIds is not null)
                foreach (var id in claimedEntThis)
                    _claimedEntityIds.TryRemove(id, out _);
            if (_claimedPhysIds is not null)
                foreach (var id in claimedPhysThis)
                    _claimedPhysIds.TryRemove(id, out _);
            if (_claimedAttIds is not null)
                foreach (var id in claimedAttThis)
                    _claimedAttIds.TryRemove(id, out _);
            throw;
        }
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
    private static int DistinctShortCount(IReadOnlyList<short> tiers)
    {
        var seen = new HashSet<short>();
        for (int i = 0; i < tiers.Count; i++) seen.Add(tiers[i]);
        return seen.Count;
    }

    internal static List<int> DistinctEntityRowIndices(
        CopyTupleParser.EntityRows ents, bool tier0Gate, out List<Hash128>? tier0Present)
    {
        var ids = CollectionsMarshal.AsSpan(ents.Ids);
        var tiers = CollectionsMarshal.AsSpan(ents.Tiers);
        var first = new List<int>(ids.Length);
        var seen = new HashSet<Hash128>(ids.Length);
        tier0Present = tier0Gate ? new List<Hash128>() : null;

        for (int i = 0; i < ids.Length; i++)
        {
            if (!seen.Add(ids[i])) continue;
            if (tier0Gate && tiers[i] == 0)
            {
                tier0Present!.Add(ids[i]);
                continue;
            }
            first.Add(i);
        }
        return first;
    }

    /// <summary>
    /// Under the apply lock: which of <paramref name="tiers"/> have at least one
    /// row in <c>laplace.entities</c>. Empty LIST(tier) leaves need no bitmap probe.
    /// </summary>
    private static async Task<HashSet<short>> NonEmptyEntityTiersAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<short> tiers, CancellationToken ct)
    {
        var distinct = new HashSet<short>();
        for (int i = 0; i < tiers.Count; i++) distinct.Add(tiers[i]);
        if (distinct.Count == 0) return distinct;

        var arr = distinct.ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "SELECT t FROM unnest($1::smallint[]) AS t "
            + "WHERE EXISTS (SELECT 1 FROM laplace.entities e WHERE e.tier = t LIMIT 1)";
        cmd.Parameters.Add(new NpgsqlParameter
        { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
        var nonempty = new HashSet<short>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            nonempty.Add(reader.GetInt16(0));
        return nonempty;
    }

    private static async Task<bool> RelationHasRowsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string relation, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        // Relation name is an internal constant (entities/physicalities/attestations), not user input.
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM laplace.{relation} LIMIT 1)";
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is true;
    }

    private readonly record struct EntityInvertResult(
        List<int> RemainingIdx, long Resolved, int RoundTrips);

    /// <summary>
    /// Under the apply lock: for each LIST(tier) leaf, if committed row count is
    /// strictly less than staged probe count for that tier, load the present id
    /// set and resolve membership locally; otherwise leave those ids on the
    /// bitmap probe path. Build the smaller side — not a fixed size dial.
    /// <paramref name="rowIdx"/> are indices into <paramref name="ents"/>.
    /// </summary>
    private static async Task<EntityInvertResult> InvertEntityTiersBySmallerSideAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
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
        var tierArr = stagedPerTier.Keys.ToArray();

        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandTimeout = 0;
            countCmd.CommandText =
                "SELECT e.tier, count(*)::bigint FROM laplace.entities e "
                + "WHERE e.tier = ANY($1) GROUP BY e.tier";
            countCmd.Parameters.Add(new NpgsqlParameter
            { Value = tierArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
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
            loadCmd.Transaction = tx;
            loadCmd.CommandTimeout = 0;
            loadCmd.CommandText = "SELECT e.id FROM laplace.entities e WHERE e.tier = ANY($1)";
            loadCmd.Parameters.Add(new NpgsqlParameter
            { Value = invertArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
            var presentInLeaf = new HashSet<Hash128>();
            await using (var idReader = await loadCmd.ExecuteReaderAsync(ct))
            {
                while (await idReader.ReadAsync(ct))
                {
                    var raw = (byte[])idReader[0];
                    if (raw.Length >= 16) presentInLeaf.Add(Hash128.FromBytes(raw));
                }
            }

            // Fast path: every staged tier inverted — no remain list, just
            // mark the rare present hits (throughput / fresh-seed shape).
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
            // Was capped at 8; a 500k-id verify is ceil(500k/131072)=4 chunks on
            // small hosts and more on large probes — let ApplyParallelism own it.
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
    /// TWO keys, because lane assignment and insert order want different things.
    ///
    /// <para><b>LaneKey</b> picks which parallel COPY connection a row rides. It must be
    /// UNIFORM or the lanes come out uneven and the widest one becomes the wall clock.
    /// Always the row id: ids are content hashes, uniform by construction.</para>
    ///
    /// <para><b>OrderKey</b> is the sort within a lane, which decides the order index
    /// pages are touched. For btree-indexed tables that is the id again — sorted ids walk
    /// PK leaves forward instead of randomly. For physicalities it is the HILBERT INDEX,
    /// because the contended index there is the coord GiST and hilbert order is its
    /// spatial locality.</para>
    ///
    /// <para>These were one key until 2026-08-04 and the collapse cost something either
    /// way. Keyed on hilbert, lanes inherited hilbert's distribution — and hilbert is
    /// locality-PRESERVING, so range-splitting it splits by region of space, and content
    /// piles at the centroid rather than spreading: one band held 60.67% of the table.
    /// Keyed on id, lanes balanced but every GiST insert became a random descent. The
    /// same property that makes hilbert a bad partition key makes it a good sort key, so
    /// the fix is to stop making it be both.</para>
    ///
    /// <para>Patch/PatchSum carry a duplicate-collapsed group's summed games/sum_score for
    /// the representative row (Patch = -1 means unpatched).</para>
    /// </summary>
    private readonly record struct KeptRow(
        CopyPartitionKey LaneKey, CopyPartitionKey OrderKey, StagedRowRef Row,
        long Patch, int CountOff, long PatchSum = 0, int SumOff = 0);

    /// <summary>
    /// Half of PackFiltered's 2 GiB ceiling, so a chunk that lands slightly over the
    /// estimate still packs. Bytes, never rows: row COUNT says nothing about payload
    /// size when one row can be a 145 MB trajectory.
    /// </summary>
    private const long MaxCopyPayloadBytes = 1L << 30;

    /// <summary>
    /// Splits a kept-range into byte-bounded COPY payloads. PackFiltered packs an
    /// entire call into ONE byte[] and refuses past 2 GiB, so the unit that matters
    /// is bytes — a TinyLlama factors layer is only 69 physicality rows but ~10 GB of
    /// trajectory, and no row-count batching avoids the ceiling. Each chunk gets its
    /// own COPY stream, which is already what CopyFilteredAsync does per call, so this
    /// changes the size of the unit and nothing about the wire protocol.
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

    private static int ResolveCopyGroups(int rowCount, int sharedSecondaryKeys)
    {
        // Id-range groups own disjoint PK leaves after LaneKey order. MEASURED
        // (Npgsql binary COPY into live entities, indexes UP, this host): 8-way
        // peaked ~591k rows/s; 12-way fell to ~534k (type-btree contention).
        // Cap at that measured peak — not the old homogeneous-2 scar.
        //
        // Floor was 16_384 → Wiktionary applies (~30–36k phys/ent) always got
        // groups=1 while phys COPY sat at ~2.4k rows/s. 4_096 fans a 30k batch
        // to 7–8 connections without waiting for a mega-flush.
        _ = sharedSecondaryKeys;
        const int MeasuredPeakGroups = 8;
        int bySize = (int)Math.Min(ApplyParallelism, Math.Max(1L, rowCount / 4_096));
        return Math.Min(bySize, MeasuredPeakGroups);
    }

    /// <summary>
    /// Map a big-endian sort key into <c>[0, groups)</c> for parallel COPY.
    /// <paramref name="groups"/> == 1 must short-circuit: C# masks ulong shifts
    /// by 6 bits, so <c>hiBe &gt;&gt; 64</c> becomes <c>&gt;&gt; 0</c>, the cast to
    /// int can be negative, and <c>counts[g]++</c> throws IndexOutOfRange.
    /// Measured 2026-08-02: Unicode second working-set kept 3,580 entities
    /// (groups=1) after the first mega-batch COPYed ~1.17M (groups=8).
    /// </summary>
    internal static int CopyGroupOf(ulong hiBe, int groups)
    {
        if (groups <= 1) return 0;
        int bits = 0;
        while ((1 << bits) < groups) bits++;
        int g = (int)(hiBe >> (64 - bits));
        if ((uint)g >= (uint)groups) g = groups - 1;
        return g;
    }

    private static byte[][] BuildSortedEntityPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        CopyTupleParser.EntityRows ents, List<int> firstIdx, int groups)
    {
        int rowCount = firstIdx.Count;
        var groupOf = new int[rowCount];
        var keysAll = new CopySortKey[rowCount];
        var counts = new int[groups];
        for (int k = 0; k < rowCount; k++)
        {
            int i = firstIdx[k];
            var key = CopySortKey.FromWire(ents.Ids[i]);
            keysAll[k] = key;
            int g = CopyGroupOf(key.HiBe, groups);
            groupOf[k] = g;
            counts[g]++;
        }
        var groupRefs = new StagedRowRef[groups][];
        var groupKeys = new CopySortKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRefs[g] = new StagedRowRef[counts[g]];
            groupKeys[g] = new CopySortKey[counts[g]];
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
        return payloads;
    }

    private static byte[][] BuildSortedCopyPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, int groups)
    {
        int rowCount = kept.Count;
        var groupOf = new int[rowCount];
        var keysAll = new CopySortKey[rowCount];
        var counts = new int[groups];
        for (int i = 0; i < rowCount; i++)
        {
            // LANE from LaneKey, ORDER from OrderKey — two different keys on purpose.
            // The lane must be uniform (id) or one connection carries the batch; the
            // order should follow the contended index (hilbert for physicalities, id
            // for the btree tables). Collapsing them forces one to lose.
            var key = CopySortKey.FromWire(kept[i].OrderKey.Wire);
            keysAll[i] = key;
            int g = CopyGroupOf(CopySortKey.FromWire(kept[i].LaneKey.Wire).HiBe, groups);
            groupOf[i] = g;
            counts[g]++;
        }
        var groupRows = new KeptRow[groups][];
        var groupKeys = new CopySortKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRows[g] = new KeptRow[counts[g]];
            groupKeys[g] = new CopySortKey[counts[g]];
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
        return payloads;
    }

    private async Task<int> CopyPayloadsParallelAsync(
        string tableName, IntentStageTable table,
        int rowCount, int groups, byte[][] payloads, long sortMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task[groups];
        for (int g = 0; g < groups; g++)
        {
            int group = g;
            tasks[g] = Task.Run(async () =>
            {
                var payload = payloads[group];
                if (payload.Length == 0) return;

                await using var conn = await _ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText =
                        "SET LOCAL session_replication_role = replica; "
                        + "SET LOCAL synchronous_commit = off; "
                        + "SET LOCAL jit = off";
                    await guc.ExecuteNonQueryAsync(ct);
                }
                string cols = IntentStage.CopyColumnList(table);
                await using (var stream = await conn.BeginRawBinaryCopyAsync(
                    $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct))
                {
                    await CopyTupleParser.WritePackedAsync(stream, payload, ct);
                }
                await tx.CommitAsync(ct);
            }, ct);
        }
        await Task.WhenAll(tasks);
        sw.Stop();
        _log.LogInformation(
            "WS_APPLY copy {Table}: {Rows:N0} rows across {Groups} id-range connection(s) in {Ms:N0}ms ({Rps:N0} rows/s; sort {SortMs:N0}ms)",
            tableName, rowCount, groups, sw.ElapsedMilliseconds,
            rowCount / Math.Max(1e-3, sw.Elapsed.TotalSeconds), sortMs);
        return 1;
    }

    private async Task<int> CopyPhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, CancellationToken ct,
        int sharedSecondaryKeys = int.MaxValue)
    {
        if (kept.Count == 0) return 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int groups = ResolveCopyGroups(kept.Count, sharedSecondaryKeys);
        // Bound StagedRowRef.Blob before pack — IndexOutOfRange inside
        // BuildSortedCopyPayloads / PackFiltered has no table name on the stack
        // (Release inlines the packer). Measured 2026-08-02: Unicode second
        // working-set on laplace-dev died here after #776 dedup cleared 23505.
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
        try
        {
            payloads = BuildSortedCopyPayloads(blobs, kept, groups);
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"COPY pack {tableName}: IndexOutOfRange in BuildSortedCopyPayloads "
                + $"(kept={kept.Count:N0}, groups={groups}, blobs={blobCount}, "
                + $"blobIndexRange=[{minBlob}..{maxBlob}], sharedSecondaryKeys={sharedSecondaryKeys})",
                ex);
        }
        long sortMs = sw.ElapsedMilliseconds;
        return await CopyPayloadsParallelAsync(
            tableName, table, kept.Count, groups, payloads, sortMs, ct);
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets,
        long[]? patchedSums, IReadOnlyList<int>? sumValueOffsets, CancellationToken ct)
    {
        // Pack BEFORE opening the stream. PackFiltered can throw — its own 2 GiB
        // ceiling is the common one — and when it threw with the COPY already open,
        // `await using` disposed the stream, the server reported that it had never
        // received a binary header, and that 22P04 "COPY file signature not
        // recognized" REPLACED the real exception on the way out. A TinyLlama
        // factors layer packing 9,547 MB reported a corrupt-looking wire protocol
        // instead of "payload exceeds 2 GiB", which is a completely different bug
        // hunt. Packing first lets the real error propagate untouched.
        byte[] packed = CopyTupleParser.PackFiltered(
            blobs, rows, patchedCounts, countValueOffsets, patchedSums, sumValueOffsets);
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await CopyTupleParser.WritePackedAsync(stream, packed, ct);
    }
}
