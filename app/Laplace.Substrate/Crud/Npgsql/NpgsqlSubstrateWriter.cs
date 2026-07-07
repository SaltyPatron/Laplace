using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlSubstrateWriter : ISubstrateWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<NpgsqlSubstrateWriter> _log;

    public NpgsqlSubstrateWriter(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlSubstrateWriter>? logger = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
    }




    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyManyAsync(new[] { change }, ct);
    }

    public Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => ApplyManyInternalAsync(changes, workingSetToken: null, ct);

    private async Task<ApplyResult> ApplyManyInternalAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128? workingSetToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sw = Stopwatch.StartNew();
        int roundTrips = 0;

        int entitiesAttempted = 0, physAttempted = 0, attAttempted = 0;
        for (int i = 0; i < changes.Count; i++)
        {
            if (!changes[i].TestimonyWalks.IsDefaultOrEmpty)
                throw new InvalidOperationException(
                    "testimony walks reached the evidence writer: walks are the consensus-only "
                    + "journal (the accumulating writer journals and strips them); evidence-"
                    + "persisting deposits emit AttestationRows at the decomposer");
            entitiesAttempted += changes[i].Entities.Length;
            physAttempted += changes[i].Physicalities.Length;
            attAttempted += changes[i].Attestations.Length;
        }
        if (changes.Count == 0)
            return new ApplyResult(0, 0, 0, 0, 0, 0, 0, sw.Elapsed, false);




        var prebuiltStages = new List<IntentStage>();
        foreach (var c in changes)
        {
            if (c.IntentStages.IsDefaultOrEmpty) continue;
            foreach (var pre in c.IntentStages)
                if (!pre.IsInvalid) prebuiltStages.Add(pre);
        }




        IntentStage? managedStage = null;
        if (entitiesAttempted > 0 || physAttempted > 0 || attAttempted > 0)
        {
            managedStage = IntentStage.New(
                Math.Max(Math.Max(entitiesAttempted, physAttempted), attAttempted));
            Span<double> coord = stackalloc double[4];
            var seenEntity = new HashSet<Hash128>();
            var seenPhys = new HashSet<Hash128>();

            foreach (var c in changes)
                foreach (var e in c.Entities)
                {
                    if (!seenEntity.Add(e.Id)) continue;
                    managedStage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
                }
            foreach (var c in changes)
                foreach (var p in c.Physicalities)
                {
                    if (!seenPhys.Add(p.Id)) continue;
                    coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                    managedStage.AddPhysicality(
                        p.Id, p.EntityId, (short)p.Type,
                        coord, p.HilbertIndex,
                        p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                                  : p.TrajectoryXyzm.AsSpan(),
                        p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
                }
            // No dedup here: duplicate attestation ids across changes carry
            // real observation counts. The apply core collapses them exactly
            // like apply_batch did (latest-ts representative, summed games)
            // instead of dropping the later observations on the floor.
            foreach (var c in changes)
                foreach (var a in c.Attestations)
                {
                    managedStage.AddAttestation(
                        a.Id, a.SubjectId, a.TypeId, a.ObjectId, a.SourceId, a.ContextId,
                        (short)a.Outcome, a.LastObservedAtUnixUs, a.ObservationCount, a.HighwayMask);
                }
        }

        var sourceStages = new List<IntentStage>(prebuiltStages.Count + 1);
        sourceStages.AddRange(prebuiltStages);
        if (managedStage is not null
            && (managedStage.EntityCount > 0 || managedStage.PhysicalityCount > 0
                || managedStage.AttestationCount > 0))
            sourceStages.Add(managedStage);

        long entCount = sourceStages.Sum(s => (long)s.EntityCount);
        long physCount = sourceStages.Sum(s => (long)s.PhysicalityCount);
        long attCount = sourceStages.Sum(s => (long)s.AttestationCount);

        int entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long attestationsFolded = 0;
        long entitiesSkipped = 0, physicalitiesSkipped = 0;
        bool anyRows = entCount > 0 || physCount > 0 || attCount > 0;

        try
        {
            if (anyRows)
            {
                var r = await ApplyStagesCoreAsync(sourceStages, workingSetToken, ct);
                entitiesInserted = r.e;
                physicalitiesInserted = r.p;
                attestationsInserted = r.a;
                attestationsFolded = r.fold;
                entitiesSkipped = r.eSkip;
                physicalitiesSkipped = r.pSkip;
                roundTrips += r.rt;

                // The apply-verify is the sole novelty gate: compose stages the
                // whole working set (content-addressed, deduped in the content
                // bank), the verify probes which ids the DB already holds, and
                // present content is skipped from COPY (present attestations fold
                // instead). Skipped rows are therefore EXPECTED — shared substrate
                // already committed by an earlier working set or source, not an
                // error and not a race. Logged at info for volume visibility.
                if (entitiesSkipped > 0 || physicalitiesSkipped > 0)
                {
                    _log.LogInformation(
                        "APPLY_PRESENT_SKIPPED entities={EntitiesSkipped} physicalities={PhysicalitiesSkipped} "
                        + "(already-present shared-substrate rows skipped from COPY by the apply-verify — expected)",
                        entitiesSkipped, physicalitiesSkipped);
                }
            }

            // Caller-owned prebuilt stages are retired ONLY on success. On a failed
            // apply the batch may be retried wholesale (IngestRunner's transient-error
            // loop re-submits the same SubstrateChange objects); disposing here on the
            // failure path turned every retry into an ObjectDisposedException that
            // masked the real error (.scratchpad/02 Issues 15/17). IntentStage is a
            // SafeHandle, so stages abandoned by a fatal abort are still reclaimed by
            // the finalizer.
            foreach (var pre in prebuiltStages) pre.Dispose();
        }
        finally
        {
            managedStage?.Dispose();
        }

        sw.Stop();

        long rowsAttempted = (long)entitiesAttempted + physAttempted + attAttempted;
        if (rowsAttempted >= 1000 && RtBudgetPer10K > 0)
        {
            long budget = 20 + RtBudgetPer10K * (rowsAttempted / 10_000 + 1);
            if (roundTrips > budget)
            {
                string msg = $"round-trip budget exceeded: {roundTrips} RT for {rowsAttempted:N0} rows "
                           + $"(budget {budget}; LAPLACE_RT_BUDGET_PER_10K={RtBudgetPer10K})";
                if (RtBudgetEnforce)
                    throw new InvalidOperationException(msg);
                _log.LogWarning("{Message}", msg);
            }
        }

        return new ApplyResult(
            EntitiesAttempted: entitiesAttempted,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesAttempted: physAttempted,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsAttempted: attAttempted,
            AttestationsInserted: attestationsInserted,
            RoundTrips: roundTrips,
            WallClock: sw.Elapsed,
            TrunkShortcircuitHit:
                !anyRows ||
                (entitiesInserted == 0 && physicalitiesInserted == 0
                 && attestationsInserted == 0 && attestationsFolded == 0),
            EntitiesSkippedAtMerge: entitiesSkipped,
            PhysicalitiesSkippedAtMerge: physicalitiesSkipped);
    }

    private static readonly long RtBudgetPer10K = 64;
    private static readonly bool RtBudgetEnforce = false;
}
