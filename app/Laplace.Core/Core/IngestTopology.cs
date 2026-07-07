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

    public int CommitWorkers { get; }

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

        int commitWorkers,

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

        CommitWorkers = commitWorkers;

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

                + "file_workers={9} compose_workers={10} commit_workers={11} apply_partitions={12}",

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

                t.CommitWorkers,

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

        int fileWorkers = CpuTopology.ResolveCpuBoundWorkers(headroom: 2);

        int composeWorkers = CpuTopology.ResolveCpuBoundWorkers(headroom: 1);

        int commitWorkers = CpuTopology.ResolveIngestCommitWorkers(headroom: 1);

        int applyPartitions = ResolveApplyPartitions();

        int pCores = CpuTopology.PerformanceCoreCount;

        var sizing = IngestSizing.Resolve(pCores, fileWorkers, applyPartitions);

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

            commitWorkers,

            applyPartitions,

            sizing);

    }



    public static int ResolveApplyPartitions() => 1;



    internal static void ResetForTests() { }

}

