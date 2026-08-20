namespace Laplace.Engine.Core;



public sealed class IngestTopology

{

    private static readonly Lazy<IngestTopology> Lazy =

        new(ResolveFresh, LazyThreadSafetyMode.ExecutionAndPublication);



    private static volatile bool _topologyLogged;



    public int PerformanceCoreCount { get; }

    public int PerformanceLogicalProcessorCount { get; }

    public int EfficientCoreCount { get; }

    public int LogicalProcessorCount { get; }

    public bool IsHybrid { get; }

    public IReadOnlyList<int> PerformanceCoreCpuIndices { get; }

    public IReadOnlyList<int> EfficientCoreCpuIndices { get; }

    public string DetectionSource { get; }

    public bool EntryThreadPinned { get; }

    public int FileWorkers { get; }

    public int ComposeWorkers { get; }

    public int IoWorkersAvailable { get; }

    /// <summary>
    /// Outer database-apply dispatch width. This remains one until claim-before-COPY is
    /// race-free; each dispatched apply still fans out internally across ApplyPartitions.
    /// </summary>
    public int ApplyDispatchWorkers { get; }

    public int ApplyPartitions { get; }

    public IngestSizing.Plan Sizing { get; }



    private IngestTopology(

        int performanceCoreCount,

        int performanceLogicalProcessorCount,

        int efficientCoreCount,

        int logicalProcessorCount,

        bool isHybrid,

        IReadOnlyList<int> performanceCoreCpuIndices,

        IReadOnlyList<int> efficientCoreCpuIndices,

        string detectionSource,

        bool entryThreadPinned,

        int fileWorkers,

        int composeWorkers,

        int ioWorkersAvailable,

        int applyDispatchWorkers,

        int applyPartitions,

        IngestSizing.Plan sizing)

    {

        PerformanceCoreCount = performanceCoreCount;

        PerformanceLogicalProcessorCount = performanceLogicalProcessorCount;

        EfficientCoreCount = efficientCoreCount;

        LogicalProcessorCount = logicalProcessorCount;

        IsHybrid = isHybrid;

        PerformanceCoreCpuIndices = performanceCoreCpuIndices;

        EfficientCoreCpuIndices = efficientCoreCpuIndices;

        DetectionSource = detectionSource;

        EntryThreadPinned = entryThreadPinned;

        FileWorkers = fileWorkers;

        ComposeWorkers = composeWorkers;

        IoWorkersAvailable = ioWorkersAvailable;

        ApplyDispatchWorkers = applyDispatchWorkers;

        ApplyPartitions = applyPartitions;

        Sizing = sizing;

    }



    public static IngestTopology Current => Lazy.Value;



    public static IngestTopology EnsureReady()

    {

        var t = Lazy.Value;

        if (!_topologyLogged)

        {

            _topologyLogged = true;

            Console.Error.WriteLine(

                "ingest_topology: source={0} hybrid={1} p_physical={2} p_logical={3} e_cores={4} logical={5} "

                + "p_primary_lps=[{6}] e_lps=[{7}] entry_pinned={8} "

                + "file_workers={9} compose_workers={10} io_workers_available={11} "
                + "apply_dispatch_workers={12} apply_partitions={13}",

                t.DetectionSource,

                t.IsHybrid.ToString().ToLowerInvariant(),

                t.PerformanceCoreCount,

                t.PerformanceLogicalProcessorCount,

                t.EfficientCoreCount,

                t.LogicalProcessorCount,

                string.Join(",", t.PerformanceCoreCpuIndices),

                string.Join(",", t.EfficientCoreCpuIndices),

                t.EntryThreadPinned.ToString().ToLowerInvariant(),

                t.FileWorkers,

                t.ComposeWorkers,

                t.IoWorkersAvailable,

                t.ApplyDispatchWorkers,

                t.ApplyPartitions);

            IngestSizing.LogPlan(t.Sizing);

        }



        if (t.IsHybrid && t.PerformanceCoreCpuIndices.Count > 0)

            CpuTopology.PinWorkerThread(0);

        return t;

    }



    private static IngestTopology ResolveFresh()

    {

        bool pinned = CpuTopology.PinCurrentThreadToPerformanceCores();

        // File pipelines and their compose fans share this one P-core pool; they do
        // not each own an independently headroom-reduced pool. Active files divide
        // ComposeWorkers in IngestDescentFlush and the tail receives the released lanes.
        int fileWorkers = CpuTopology.ResolveCpuBoundWorkers();

        int composeWorkers = CpuTopology.ResolveCpuBoundWorkers();

        int ioWorkersAvailable = CpuTopology.ResolveIoBoundWorkers();

        const int applyDispatchWorkers = 1;

        // Read the P-core count EXACTLY ONCE and derive apply partitions from that same
        // value. Previously applyPartitions came from ResolveApplyPartitions() (its own
        // read of CpuTopology.PerformanceCoreCount) while pCores read it again separately;
        // a first-touch topology-init race made the two reads disagree — apply_partitions=1
        // while p_physical=8 — which silently pinned the ENTIRE apply COPY path to a single
        // thread (parallelCopy in NpgsqlWorkingSetApply requires ApplyParallelism > 1). One
        // read → the two can never diverge again.
        int pCores = CpuTopology.PerformanceCoreCount;

        int applyPartitions = Math.Max(1, pCores);

        var sizing = IngestSizing.Resolve(
            pCores, fileWorkers, applyPartitions, composeWorkers: composeWorkers);

        return new IngestTopology(

            pCores,

            CpuTopology.PerformanceLogicalProcessorCount,

            CpuTopology.EfficientCoreCount,

            CpuTopology.LogicalProcessorCount,

            CpuTopology.IsHybrid,

            CpuTopology.PerformanceCoreCpuIndices,

            CpuTopology.EfficientCoreCpuIndices,

            CpuTopology.DetectionSource,

            pinned,

            fileWorkers,

            composeWorkers,

            ioWorkersAvailable,

            applyDispatchWorkers,

            applyPartitions,

            sizing);

    }



    public static int ResolveApplyDispatchWorkers() => Current.ApplyDispatchWorkers;



    internal static void ResetForTests() { }

}
