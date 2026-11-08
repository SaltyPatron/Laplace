using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static partial class CpuTopology
{
    public static int PerformanceCoreCount => Current.PerformanceCoreCount;

    public static int EfficientCoreCount => Current.EfficientCoreCount;

    public static int LogicalProcessorCount => Current.LogicalProcessorCount;

    public static bool IsHybrid => Current.IsHybrid;




    public static int PerformanceLogicalProcessorCount => Pools.PrimaryPLogicalCount;




    public static IReadOnlyList<int> PerformanceCoreCpuIndices

    {

        get

        {

            if (TestPCoreIndicesOverride is not null) return TestPCoreIndicesOverride;

            if (TestOverride is not null) return Array.Empty<int>();

            return Pools.PrimaryPCoreGlobalIndices;

        }

    }




    public static IReadOnlyList<int> EfficientCoreCpuIndices

    {

        get

        {

            if (TestECoreIndicesOverride is not null) return TestECoreIndicesOverride;

            if (TestOverride is not null) return Array.Empty<int>();

            return Pools.EfficientCoreGlobalIndices;

        }

    }



    public static string DetectionSource => Pools.Source;



    internal static CpuSnapshot? TestOverride;

    internal static int[]? TestPCoreIndicesOverride;

    internal static int[]? TestECoreIndicesOverride;

    internal static TopologyPools? TestPoolsOverride;



    // LazySnapshot caches the first snapshot forever, so an installed pools override
    // derives the snapshot uncached; that path is taken only under an override.
    private static CpuSnapshot Current =>
        TestOverride ?? (TestPoolsOverride is not null ? DetectPlatform() : LazySnapshot.Value);

    private static TopologyPools Pools => TestPoolsOverride ?? LazyPools.Value;



    private static readonly Lazy<CpuSnapshot> LazySnapshot =

        new(DetectPlatform, LazyThreadSafetyMode.ExecutionAndPublication);



    private static readonly Lazy<TopologyPools> LazyPools =

        new(DetectPools, LazyThreadSafetyMode.ExecutionAndPublication);



    public static int ResolveCpuBoundWorkers()

    {

        int physicalP = Math.Max(1, PerformanceCoreCount);

        return physicalP;

    }




    public static int ResolveIngestCommitWorkers()

    {

        if (IsHybrid && EfficientCoreCpuIndices.Count > 0)

            return Math.Max(1, EfficientCoreCpuIndices.Count);

        return Math.Max(1, LogicalProcessorCount);

    }




    public static int ResolveIoBoundWorkers() => ResolveIngestCommitWorkers();




    public static int ResolveApplyPartitions() => Math.Max(1, PerformanceCoreCount);




    // Maintenance is CPU-bound and uses the same physical P-core pool as parallel query;
    // PostgresResourcePlan budgets backend memory for every parallel owner.
    public static int ParallelMaintenanceWorkers => Math.Max(1, PerformanceCoreCount);



    public static bool TryPinCurrentThreadToPerformanceCores() => PinCurrentThreadToPerformanceCores();



    public static void EnsurePerformanceCoreExecution()

    {

        if (PinDisabled) return;

        if (!IsHybrid) return;

        if (PerformanceCoreCpuIndices.Count == 0)

        {

            Console.Error.WriteLine("warning: hybrid CPU but P-core pool unknown — running unpinned");

            return;

        }

        if (!TryPinCurrentThreadToPerformanceCores())

            Console.Error.WriteLine("warning: hybrid CPU but P-core pin failed — running unpinned");

    }



    public static void RequirePerformanceCorePin() => EnsurePerformanceCoreExecution();



    public static bool PinCurrentThreadToPerformanceCores()

    {

        if (PinDisabled) return false;

        var ids = Pools.PrimaryPCoreCpuSetIds;

        if (ids.Length > 0 && TrySetThreadCpuSets(ids))

            return true;



        var aff = Pools.PrimaryPCoreAffinities;

        if (aff.Length == 0) return false;



        if (OperatingSystem.IsWindows())

            return TryPinWindowsCombined(aff);

        if (OperatingSystem.IsLinux())

            return TryPinLinux(PerformanceCoreCpuIndices);

        return false;

    }




    public static void PinWorkerThread(int workerIndex) => PinWorkerThreadCore(workerIndex, cpuBound: true);




    public static void PinIoWorkerThread(int workerIndex) => PinWorkerThreadCore(workerIndex, cpuBound: false);



    public static void RunPinnedParallel(int count, int workers, Action<int> body)
        => RunPinnedParallel(count, workers, body, PinWorkerThread);

    internal static void RunPinnedParallel(int count, int workers, Action<int> body, Action<int> initializeWorker,
        Action<Thread>? startWorker = null)

    {

        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(initializeWorker);

        if (count <= 0) return;

        workers = Math.Clamp(workers, 1, count);



        if (workers == 1)

        {

            initializeWorker(0);

            for (int i = 0; i < count; i++) body(i);

            return;

        }



        var threads = new Thread[workers];

        var next = -1;

        var errors = new Exception?[workers];

        for (int w = 0; w < workers; w++)

        {

            int workerId = w;

            threads[w] = new Thread(() =>

            {

                try
                {
                    initializeWorker(workerId);
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < count) body(i);
                }
                catch (Exception ex) { errors[workerId] = ex; }

            })
            { IsBackground = true, Name = $"laplace-pcore-{w}" };

        }

        var startupFailure = StartAndJoinWorkers(threads, startWorker);
        ThrowWorkerFailures(errors.Append(startupFailure));

    }



    public static async Task RunPinnedAsyncParallel(int count, Func<int, CancellationToken, Task> body, CancellationToken ct)

    {

        ArgumentNullException.ThrowIfNull(body);

        if (count <= 0) return;

        var threads = new Thread[count];

        var errors = new Exception?[count];

        for (int i = 0; i < count; i++)

        {

            int idx = i;

            threads[idx] = new Thread(() =>

            {

                try

                {

                    PinWorkerThread(idx);

                    body(idx, ct).GetAwaiter().GetResult();

                }

                catch (Exception ex) { errors[idx] = ex; }

            })
            { IsBackground = true, Name = $"laplace-pcore-async-{idx}" };

        }

        var startupFailure = StartAndJoinWorkers(threads);
        ThrowWorkerFailures(errors.Append(startupFailure), ct);
        ct.ThrowIfCancellationRequested();

        await Task.CompletedTask;

    }

    internal static Exception? StartAndJoinWorkers(Thread[] threads, Action<Thread>? startWorker = null)
    {
        List<Exception>? failures = null;
        try
        {
            foreach (var thread in threads)
                if (startWorker is null) thread.Start();
                else startWorker(thread);
        }
        catch (Exception failure) { failures = [failure]; }
        foreach (var thread in threads)
        {
            if ((thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) continue;
            while (true)
            {
                try { thread.Join(); break; }
                catch (ThreadInterruptedException failure)
                {
                    // Interruption is a caller failure, not permission to abandon
                    // already-running workers that still own native/arena state.
                    (failures ??= []).Add(failure);
                }
            }
        }
        return failures?.Count switch
        {
            null or 0 => null,
            1 => failures[0],
            _ => new AggregateException("Pinned worker startup/join failed.", failures)
        };
    }

    private static void ThrowWorkerFailures(IEnumerable<Exception?> errors, CancellationToken caller = default)
    {
        var failures = errors.OfType<Exception>()
            .Where(error => error is not OperationCanceledException || !caller.IsCancellationRequested)
            .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (failures.Length > 1) throw new AggregateException("Multiple pinned workers failed.", failures);
        if (failures.Length == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }



    public static Task RunOnPinnedThread(Func<CancellationToken, Task> body, string threadName, CancellationToken ct)

    {

        ArgumentNullException.ThrowIfNull(body);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>

        {

            try

            {

                PinWorkerThread(0);

                body(ct).GetAwaiter().GetResult();

                tcs.SetResult();

            }

            catch (Exception ex) { tcs.SetException(ex); }

        })
        { IsBackground = true, Name = threadName };

        thread.Start();

        return tcs.Task;

    }



    internal static CpuSnapshot Detect() => DetectPlatform();



    private static void PinWorkerThreadCore(int workerIndex, bool cpuBound)

    {

        if (PinDisabled) return;

        if (cpuBound)

        {

            var ids = Pools.PrimaryPCoreCpuSetIds;

            if (ids.Length > 0 && TrySetThreadCpuSets(new[] { ids[workerIndex % ids.Length] }))

                return;

            var aff = Pools.PrimaryPCoreAffinities;

            if (aff.Length > 0)

            {

                var a = aff[workerIndex % aff.Length];

                if (TryPinThreadToProcessor(a.Group, a.Mask)) return;

            }

            EnsurePerformanceCoreExecution();

            return;

        }



        var eIds = Pools.EfficientCoreCpuSetIds;

        if (eIds.Length > 0 && TrySetThreadCpuSets(new[] { eIds[workerIndex % eIds.Length] }))

            return;

        var eAff = Pools.EfficientCoreAffinities;

        if (eAff.Length > 0)

        {

            var a = eAff[workerIndex % eAff.Length];

            if (TryPinThreadToProcessor(a.Group, a.Mask)) return;

        }

    }



    private static bool TrySetThreadCpuSets(ReadOnlySpan<uint> cpuSetIds)

    {

        if (!OperatingSystem.IsWindows() || cpuSetIds.Length == 0) return false;

        return SetThreadSelectedCpuSets(GetCurrentThread(), cpuSetIds.ToArray(), (uint)cpuSetIds.Length);

    }



    private static TopologyPools DetectPools()

    {

        if (TestPoolsOverride is not null) return TestPoolsOverride;



        try

        {

            if (OperatingSystem.IsWindows() && TryDetectWindowsCpuSetPools(out var win))

                return ApplyOverrides(win);

            if (OperatingSystem.IsWindows() && TryDetectWindowsGlpiexPools(out var glpi))

                return ApplyOverrides(glpi);

            if (OperatingSystem.IsLinux() && TryDetectLinuxSysfsPools(out var lin))

                return ApplyOverrides(lin);

            // Non-hybrid Linux and containers: /sys/devices/cpu_core/cpus exists only on
            // hybrid P/E parts, so the generic per-CPU topology resolves physical cores
            // (not SMT threads) everywhere else.
            if (OperatingSystem.IsLinux() && TryDetectLinuxGenericSysfsPools(out var gen))

                return ApplyOverrides(gen);

        }

        catch (Exception ex)
        {
            // A detection failure leaves a uniform logical-count topology that mis-sizes
            // every worker pool, so it is reported rather than swallowed.
            Console.Error.WriteLine(
                $"cpu_topology: detection FAILED, falling back to uniform logical "
                + $"({Environment.ProcessorCount} cores) — worker pools will be mis-sized. "
                + $"{ex.GetType().Name}: {ex.Message}");
        }



        int logical = Environment.ProcessorCount;

        return ApplyOverrides(TopologyPools.Uniform(logical, "fallback-logical"));

    }

    // --- Operator overrides (env-driven, read once) ----------------------------------------
    // LAPLACE_EXCLUDE_LPS: comma list of primary logical-processor indices (as logged in
    //   p_primary_lps) removed from the compute pool. Work is never pinned there, and worker
    //   sizing shrinks with the pool; other processes still use the core.
    // LAPLACE_NO_PIN: 1/true/yes disables affinity pinning, leaving placement across P+E to
    //   the OS scheduler.
    internal static readonly bool PinDisabled =
        EnvFlag.IsSet("LAPLACE_NO_PIN");

    private static readonly HashSet<int> ExcludedLps = ParseExcludedLps();

    private static HashSet<int> ParseExcludedLps()
    {
        var set = new HashSet<int>();
        var raw = Environment.GetEnvironmentVariable("LAPLACE_EXCLUDE_LPS");
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int lp)) set.Add(lp);
        return set;
    }

    private static TopologyPools ApplyOverrides(TopologyPools p)
    {
        if (ExcludedLps.Count == 0) return p;

        FilterPool(p.PrimaryPCoreGlobalIndices, p.PrimaryPCoreCpuSetIds, p.PrimaryPCoreAffinities,
                   out var pIdx, out var pIds, out var pAff);
        FilterPool(p.EfficientCoreGlobalIndices, p.EfficientCoreCpuSetIds, p.EfficientCoreAffinities,
                   out var eIdx, out var eIds, out var eAff);

        int newPhysP = Math.Max(1, pIdx.Length);
        int ratio = p.PhysicalPCores > 0 ? Math.Max(1, p.PrimaryPLogicalCount / p.PhysicalPCores) : 1;
        int newPLogical = Math.Max(newPhysP, pIdx.Length * ratio);

        Console.Error.WriteLine(
            "cpu_topology: LAPLACE_EXCLUDE_LPS dropped [{0}]; compute P-pool now [{1}] ({2} P-core(s))",
            string.Join(",", ExcludedLps.OrderBy(x => x)),
            string.Join(",", pIdx),
            pIdx.Length);

        return new TopologyPools(
            isHybrid: p.IsHybrid,
            physicalPCores: newPhysP,
            physicalECores: eIdx.Length,
            logicalCount: p.LogicalCount,
            primaryPLogicalCount: newPLogical,
            primaryPCoreGlobalIndices: pIdx,
            primaryPCoreCpuSetIds: pIds,
            primaryPCoreAffinities: pAff,
            efficientCoreGlobalIndices: eIdx,
            efficientCoreCpuSetIds: eIds,
            efficientCoreAffinities: eAff,
            source: p.Source + "+excl");
    }

    private static void FilterPool(
        int[] idx, uint[] ids, ProcessorAffinity[] aff,
        out int[] fIdx, out uint[] fIds, out ProcessorAffinity[] fAff)
    {
        bool haveIds = ids.Length == idx.Length;
        bool haveAff = aff.Length == idx.Length;
        var ki = new List<int>(idx.Length);
        var kd = new List<uint>(idx.Length);
        var ka = new List<ProcessorAffinity>(idx.Length);
        for (int i = 0; i < idx.Length; i++)
        {
            if (ExcludedLps.Contains(idx[i])) continue;
            ki.Add(idx[i]);
            if (haveIds) kd.Add(ids[i]);
            if (haveAff) ka.Add(aff[i]);
        }
        fIdx = ki.ToArray();
        fIds = haveIds ? kd.ToArray() : Array.Empty<uint>();
        fAff = haveAff ? ka.ToArray() : Array.Empty<ProcessorAffinity>();
    }
}
