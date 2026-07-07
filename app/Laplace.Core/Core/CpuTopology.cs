using System.Numerics;

using System.Runtime.InteropServices;



namespace Laplace.Engine.Core;








public static class CpuTopology

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



    private static CpuSnapshot Current => TestOverride ?? LazySnapshot.Value;

    private static TopologyPools Pools => TestPoolsOverride ?? LazyPools.Value;



    private static readonly Lazy<CpuSnapshot> LazySnapshot =

        new(DetectPlatform, LazyThreadSafetyMode.ExecutionAndPublication);



    private static readonly Lazy<TopologyPools> LazyPools =

        new(DetectPools, LazyThreadSafetyMode.ExecutionAndPublication);



    public static int ResolveCpuBoundWorkers(int headroom = 1, int? maxCap = null)

    {

        int physicalP = Math.Max(1, PerformanceCoreCount);

        int cap = maxCap ?? physicalP;

        return Math.Clamp(physicalP - headroom, 1, cap);

    }




    public static int ResolveIngestCommitWorkers(int headroom = 1)

    {

        if (IsHybrid && EfficientCoreCpuIndices.Count > 0)

            return Math.Max(1, EfficientCoreCpuIndices.Count - headroom);

        return Math.Max(1, LogicalProcessorCount - headroom);

    }




    public static int ResolveIoBoundWorkers(int headroom = 1) => ResolveIngestCommitWorkers(headroom);




    public static int ResolveApplyPartitions() => Math.Max(1, PerformanceCoreCount);




    // Parallel workers for maintenance operations (index builds, vacuum), matching
    // tune-pg's max_parallel_maintenance_workers = (P-cores+1)/2. Declared ONCE here so no
    // call site hardcodes a literal like "4" — the P-vs-E policy for maintenance parallelism
    // lives with the topology authority, not scattered across the CRUD writer.
    public static int ParallelMaintenanceWorkers => Math.Max(1, (PerformanceCoreCount + 1) / 2);



    public static bool TryPinCurrentThreadToPerformanceCores() => PinCurrentThreadToPerformanceCores();



    public static void EnsurePerformanceCoreExecution()

    {

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

    {

        ArgumentNullException.ThrowIfNull(body);

        if (count <= 0) return;

        workers = Math.Clamp(workers, 1, count);



        if (workers == 1)

        {

            PinWorkerThread(0);

            for (int i = 0; i < count; i++) body(i);

            return;

        }



        var threads = new Thread[workers];

        var next = -1;

        Exception? failure = null;

        for (int w = 0; w < workers; w++)

        {

            int workerId = w;

            threads[w] = new Thread(() =>

            {

                PinWorkerThread(workerId);

                int i;

                while ((i = Interlocked.Increment(ref next)) < count)

                {

                    try { body(i); }

                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); return; }

                }

            })
            { IsBackground = true, Name = $"laplace-pcore-{w}" };

            threads[w].Start();

        }

        foreach (var t in threads) t.Join();

        if (failure is not null) throw failure;

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

            threads[idx].Start();

        }

        foreach (var t in threads) t.Join();

        ct.ThrowIfCancellationRequested();

        foreach (var ex in errors)

            if (ex is not null) throw ex;

        await Task.CompletedTask;

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

                return win;

            if (OperatingSystem.IsWindows() && TryDetectWindowsGlpiexPools(out var glpi))

                return glpi;

            if (OperatingSystem.IsLinux() && TryDetectLinuxSysfsPools(out var lin))

                return lin;

        }

        catch (Exception ex)
        {
            // NEVER silent: a swallowed detection failure collapses the topology to a
            // uniform fallback, which silently mis-sizes every ingest worker pool
            // (apply partitions, compose/commit workers) and single-threads the pipeline
            // with no visible cause. Surface it so a wrong core count is diagnosable.
            Console.Error.WriteLine(
                $"cpu_topology: detection FAILED, falling back to uniform logical "
                + $"({Environment.ProcessorCount} cores) — worker pools will be mis-sized. "
                + $"{ex.GetType().Name}: {ex.Message}");
        }



        int logical = Environment.ProcessorCount;

        return TopologyPools.Uniform(logical, "fallback-logical");

    }



    private static CpuSnapshot DetectPlatform()

    {

        var pools = Pools;

        if (pools.IsHybrid)

            return new CpuSnapshot(pools.PhysicalPCores, pools.PhysicalECores, pools.LogicalCount, true);



        int logical = Environment.ProcessorCount;

        return new CpuSnapshot(logical, 0, logical, IsHybrid: false);

    }







    private const uint CpuSetInformationType = 0;



    private static bool TryDetectWindowsCpuSetPools(out TopologyPools pools)

    {

        pools = default;

        uint size = 0;

        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, ref size, GetCurrentProcess(), 0);

        if (size == 0) return false;



        IntPtr buffer = Marshal.AllocHGlobal((int)size);

        try

        {

            if (!GetSystemCpuSetInformation(buffer, size, ref size, GetCurrentProcess(), 0))

                return false;



            var entries = new List<CpuSetEntry>();

            int offset = 0;

            while (offset < (int)size)

            {

                uint entrySize = (uint)Marshal.ReadInt32(buffer, offset);

                if (entrySize == 0) break;

                uint type = (uint)Marshal.ReadInt32(buffer, offset + 4);

                if (type == CpuSetInformationType)

                {

                    int b = offset + 8;

                    entries.Add(new CpuSetEntry(

                        Id: (uint)Marshal.ReadInt32(buffer, b),

                        Group: (ushort)Marshal.ReadInt16(buffer, b + 4),

                        LogicalProcessorIndex: Marshal.ReadByte(buffer, b + 6),

                        CoreIndex: Marshal.ReadByte(buffer, b + 7),

                        EfficiencyClass: Marshal.ReadByte(buffer, b + 10)));

                }

                offset += (int)entrySize;

            }



            if (entries.Count == 0) return false;

            return TryBuildPoolsFromCpuSets(entries, "windows-cpuset", out pools);

        }

        finally

        {

            Marshal.FreeHGlobal(buffer);

        }

    }



    private static bool TryBuildPoolsFromCpuSets(List<CpuSetEntry> entries, string source, out TopologyPools pools)

    {

        pools = default;

        byte maxEff = entries.Max(e => e.EfficiencyClass);

        byte minEff = entries.Min(e => e.EfficiencyClass);

        bool hybrid = maxEff != minEff;





        var primaryP = entries

            .Where(e => e.EfficiencyClass == maxEff)

            .GroupBy(e => (e.Group, e.CoreIndex))

            .Select(g => g.OrderBy(e => e.LogicalProcessorIndex).First())

            .OrderBy(e => e.GlobalIndex)

            .ToArray();





        var eCores = entries

            .Where(e => hybrid && e.EfficiencyClass < maxEff)

            .OrderBy(e => e.GlobalIndex)

            .ToArray();



        if (primaryP.Length == 0) return false;





        if (!hybrid)

        {

            primaryP = entries

                .GroupBy(e => (e.Group, e.CoreIndex))

                .Select(g => g.OrderBy(e => e.LogicalProcessorIndex).First())

                .OrderBy(e => e.GlobalIndex)

                .ToArray();

            eCores = Array.Empty<CpuSetEntry>();

        }



        int pLogical = entries.Count(e => e.EfficiencyClass == maxEff);



        pools = new TopologyPools(

            isHybrid: hybrid,

            physicalPCores: primaryP.Length,

            physicalECores: eCores.Length,

            logicalCount: entries.Count,

            primaryPLogicalCount: hybrid ? pLogical : primaryP.Length,

            primaryPCoreGlobalIndices: primaryP.Select(e => e.GlobalIndex).ToArray(),

            primaryPCoreCpuSetIds: primaryP.Select(e => e.Id).ToArray(),

            primaryPCoreAffinities: primaryP.Select(e => e.ToAffinity()).ToArray(),

            efficientCoreGlobalIndices: eCores.Select(e => e.GlobalIndex).ToArray(),

            efficientCoreCpuSetIds: eCores.Select(e => e.Id).ToArray(),

            efficientCoreAffinities: eCores.Select(e => e.ToAffinity()).ToArray(),

            source: source);

        return true;

    }







    private static bool TryDetectWindowsGlpiexPools(out TopologyPools pools)

    {

        pools = default;

        const int RelationProcessorCore = 0;

        uint size = 0;

        if (!GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size)

            && Marshal.GetLastWin32Error() != 122)

            return false;



        IntPtr buffer = Marshal.AllocHGlobal((int)size);

        try

        {

            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))

                return false;



            int offset = 0;

            var pPrimary = new List<ProcessorAffinity>();

            var ePrimary = new List<ProcessorAffinity>();

            int pLogical = 0;



            while (offset < (int)size)

            {

                var header = Marshal.PtrToStructure<LogicalProcessorInfoHeader>(buffer + offset);

                if (header.Relationship == RelationProcessorCore)

                {

                    int relOffset = offset + Marshal.SizeOf<LogicalProcessorInfoHeader>();

                    byte effClass = Marshal.ReadByte(buffer, relOffset + 1);

                    ushort groupCount = (ushort)Marshal.ReadInt16(buffer, relOffset + 22);

                    if (groupCount >= 1)

                    {

                        int groupOffset = relOffset + 24;

                        for (int g = 0; g < groupCount; g++)

                        {

                            int entryOffset = groupOffset + g * 16;

                            ulong mask = (ulong)Marshal.ReadInt64(buffer, entryOffset);

                            ushort group = (ushort)Marshal.ReadInt16(buffer, entryOffset + 8);

                            int lpCount = BitOperations.PopCount(mask);

                            pLogical += lpCount;

                            if (mask == 0) continue;

                            int bit = BitOperations.TrailingZeroCount(mask);

                            var primary = new ProcessorAffinity(group, 1UL << bit);

                            if (effClass > 0)

                                pPrimary.Add(primary);

                            else

                                ePrimary.Add(primary);

                        }

                    }

                }

                if (header.Size == 0) break;

                offset += (int)header.Size;

            }



            if (pPrimary.Count == 0 && ePrimary.Count == 0)

            {



                offset = 0;

                var cores = new List<(ulong Mask, int LpCount, ushort Group, byte Eff)>();

                while (offset < (int)size)

                {

                    var header = Marshal.PtrToStructure<LogicalProcessorInfoHeader>(buffer + offset);

                    if (header.Relationship == RelationProcessorCore)

                    {

                        int relOffset = offset + Marshal.SizeOf<LogicalProcessorInfoHeader>();

                        ushort groupCount = (ushort)Marshal.ReadInt16(buffer, relOffset + 22);

                        if (groupCount >= 1)

                        {

                            int groupOffset = relOffset + 24;

                            ulong mask = (ulong)Marshal.ReadInt64(buffer, groupOffset);

                            ushort group = (ushort)Marshal.ReadInt16(buffer, groupOffset + 8);

                            int lp = BitOperations.PopCount(mask);

                            cores.Add((mask, lp, group, Marshal.ReadByte(buffer, relOffset + 1)));

                        }

                    }

                    if (header.Size == 0) break;

                    offset += (int)header.Size;

                }



                if (cores.Count == 0) return false;

                int maxLp = cores.Max(c => c.LpCount);

                int minLp = cores.Where(c => c.LpCount > 0).Select(c => c.LpCount).DefaultIfEmpty(maxLp).Min();

                bool hybrid = maxLp > minLp;

                pLogical = cores.Where(c => c.LpCount == maxLp).Sum(c => c.LpCount);



                foreach (var c in cores)

                {

                    if (c.Mask == 0) continue;

                    int bit = BitOperations.TrailingZeroCount(c.Mask);

                    var primary = new ProcessorAffinity(c.Group, 1UL << bit);

                    if (hybrid && c.LpCount == maxLp) pPrimary.Add(primary);

                    else if (hybrid) ePrimary.Add(primary);

                    else pPrimary.Add(primary);

                }

            }



            pPrimary.Sort((a, b) => a.GlobalIndex.CompareTo(b.GlobalIndex));

            ePrimary.Sort((a, b) => a.GlobalIndex.CompareTo(b.GlobalIndex));

            if (pPrimary.Count == 0) return false;



            bool isHybrid = ePrimary.Count > 0 || pPrimary.Count < pLogical;

            pools = new TopologyPools(

                isHybrid: isHybrid,

                physicalPCores: pPrimary.Count,

                physicalECores: ePrimary.Count,

                logicalCount: Environment.ProcessorCount,

                primaryPLogicalCount: Math.Max(pPrimary.Count, pLogical),

                primaryPCoreGlobalIndices: pPrimary.Select(a => a.GlobalIndex).ToArray(),

                primaryPCoreCpuSetIds: Array.Empty<uint>(),

                primaryPCoreAffinities: pPrimary.ToArray(),

                efficientCoreGlobalIndices: ePrimary.Select(a => a.GlobalIndex).ToArray(),

                efficientCoreCpuSetIds: Array.Empty<uint>(),

                efficientCoreAffinities: ePrimary.ToArray(),

                source: "windows-glpiex");

            return true;

        }

        finally

        {

            Marshal.FreeHGlobal(buffer);

        }

    }







    private static bool TryDetectLinuxSysfsPools(out TopologyPools pools)

    {

        pools = default;

        const string pPath = "/sys/devices/cpu_core/cpus";

        const string ePath = "/sys/devices/cpu_atom/cpus";

        if (!File.Exists(pPath)) return false;



        try

        {

            var pAll = ParseCpuList(File.ReadAllText(pPath).Trim());

            var pPrimary = DedupeLinuxPrimaryCores(pAll);

            var eAll = File.Exists(ePath) ? ParseCpuList(File.ReadAllText(ePath).Trim()) : Array.Empty<int>();

            bool hybrid = eAll.Length > 0;



            pools = new TopologyPools(

                isHybrid: hybrid,

                physicalPCores: pPrimary.Length,

                physicalECores: eAll.Length,

                logicalCount: Environment.ProcessorCount,

                primaryPLogicalCount: pAll.Length,

                primaryPCoreGlobalIndices: pPrimary,

                primaryPCoreCpuSetIds: Array.Empty<uint>(),

                primaryPCoreAffinities: pPrimary.Select(i => new ProcessorAffinity((ushort)(i / 64), 1UL << (i % 64))).ToArray(),

                efficientCoreGlobalIndices: eAll,

                efficientCoreCpuSetIds: Array.Empty<uint>(),

                efficientCoreAffinities: eAll.Select(i => new ProcessorAffinity((ushort)(i / 64), 1UL << (i % 64))).ToArray(),

                source: "linux-sysfs");

            return pPrimary.Length > 0;

        }

        catch { return false; }

    }



    internal static int[] DedupeLinuxPrimaryCores(int[] cpuList)

    {

        var seen = new HashSet<int>();

        var primaries = new List<int>();

        foreach (int cpu in cpuList.OrderBy(c => c))

        {

            string coreIdPath = $"/sys/devices/system/cpu/cpu{cpu}/topology/core_id";

            if (!File.Exists(coreIdPath)) { primaries.Add(cpu); continue; }

            if (!int.TryParse(File.ReadAllText(coreIdPath).Trim(), out int coreId)) { primaries.Add(cpu); continue; }

            if (seen.Add(coreId)) primaries.Add(cpu);

        }

        return primaries.ToArray();

    }



    internal static int[] ParseCpuList(string s)

    {

        var result = new List<int>();

        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))

        {

            int dash = part.IndexOf('-');

            if (dash < 0)

            {

                if (int.TryParse(part, out int v)) result.Add(v);

            }

            else if (int.TryParse(part[..dash], out int lo) && int.TryParse(part[(dash + 1)..], out int hi))

            {

                for (int i = lo; i <= hi; i++) result.Add(i);

            }

        }

        result.Sort();

        return result.ToArray();

    }







    private static bool TryPinWindowsCombined(ProcessorAffinity[] affinities)

    {

        foreach (var grp in affinities.GroupBy(a => a.Group))

        {

            ulong combined = 0;

            foreach (var a in grp) combined |= a.Mask;

            if (!TryPinThreadToProcessor(grp.Key, combined)) return false;

        }

        return true;

    }



    private static bool TryPinThreadToProcessor(ushort group, ulong mask)

    {

        if (mask == 0) return false;

        if (OperatingSystem.IsWindows())

        {

            if (group == 0)

            {

                IntPtr prior = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);

                return prior != IntPtr.Zero;

            }

            var ga = new GroupAffinity { Mask = (UIntPtr)mask, Group = group };

            return SetThreadGroupAffinity(GetCurrentThread(), ref ga, out _);

        }

        if (OperatingSystem.IsLinux())

        {

            int cpu = (int)(group * 64 + BitOperations.TrailingZeroCount(mask));

            const int setBytes = 128;

            var set = new byte[setBytes];

            if (cpu < 0 || cpu >= setBytes * 8) return false;

            set[cpu >> 3] |= (byte)(1 << (cpu & 7));

            return sched_setaffinity(0, (IntPtr)setBytes, set) == 0;

        }

        return false;

    }



    private static bool TryPinLinux(IReadOnlyList<int> cpuIndices)

    {

        const int setBytes = 128;

        var set = new byte[setBytes];

        foreach (int i in cpuIndices)

        {

            if (i < 0 || i >= setBytes * 8) continue;

            set[i >> 3] |= (byte)(1 << (i & 7));

        }

        return sched_setaffinity(0, (IntPtr)setBytes, set) == 0;

    }



    internal readonly record struct ProcessorAffinity(ushort Group, ulong Mask)

    {

        public int GlobalIndex => Group * 64 + BitOperations.TrailingZeroCount(Mask);

    }



    private readonly record struct CpuSetEntry(

        uint Id, ushort Group, byte LogicalProcessorIndex, byte CoreIndex, byte EfficiencyClass)

    {

        public int GlobalIndex => Group * 64 + LogicalProcessorIndex;

        public ProcessorAffinity ToAffinity() => new(Group, 1UL << LogicalProcessorIndex);

    }



    internal sealed class TopologyPools

    {

        public bool IsHybrid { get; }

        public int PhysicalPCores { get; }

        public int PhysicalECores { get; }

        public int LogicalCount { get; }

        public int PrimaryPLogicalCount { get; }

        public int[] PrimaryPCoreGlobalIndices { get; }

        public uint[] PrimaryPCoreCpuSetIds { get; }

        public ProcessorAffinity[] PrimaryPCoreAffinities { get; }

        public int[] EfficientCoreGlobalIndices { get; }

        public uint[] EfficientCoreCpuSetIds { get; }

        public ProcessorAffinity[] EfficientCoreAffinities { get; }

        public string Source { get; }



        public TopologyPools(

            bool isHybrid, int physicalPCores, int physicalECores, int logicalCount,

            int primaryPLogicalCount,

            int[] primaryPCoreGlobalIndices, uint[] primaryPCoreCpuSetIds,

            ProcessorAffinity[] primaryPCoreAffinities,

            int[] efficientCoreGlobalIndices, uint[] efficientCoreCpuSetIds,

            ProcessorAffinity[] efficientCoreAffinities, string source)

        {

            IsHybrid = isHybrid;

            PhysicalPCores = physicalPCores;

            PhysicalECores = physicalECores;

            LogicalCount = logicalCount;

            PrimaryPLogicalCount = primaryPLogicalCount;

            PrimaryPCoreGlobalIndices = primaryPCoreGlobalIndices;

            PrimaryPCoreCpuSetIds = primaryPCoreCpuSetIds;

            PrimaryPCoreAffinities = primaryPCoreAffinities;

            EfficientCoreGlobalIndices = efficientCoreGlobalIndices;

            EfficientCoreCpuSetIds = efficientCoreCpuSetIds;

            EfficientCoreAffinities = efficientCoreAffinities;

            Source = source;

        }



        public static TopologyPools Uniform(int logical, string source) => new(

            isHybrid: false, physicalPCores: logical, physicalECores: 0, logicalCount: logical,

            primaryPLogicalCount: logical,

            primaryPCoreGlobalIndices: Enumerable.Range(0, logical).ToArray(),

            primaryPCoreCpuSetIds: Array.Empty<uint>(),

            primaryPCoreAffinities: Enumerable.Range(0, logical)

                .Select(i => new ProcessorAffinity(0, 1UL << i)).ToArray(),

            efficientCoreGlobalIndices: Array.Empty<int>(),

            efficientCoreCpuSetIds: Array.Empty<uint>(),

            efficientCoreAffinities: Array.Empty<ProcessorAffinity>(),

            source: source);

    }



    [StructLayout(LayoutKind.Sequential)]

    private struct GroupAffinity

    {

        public UIntPtr Mask;

        public ushort Group;

        private ushort _reserved0, _reserved1, _reserved2;

    }



    [StructLayout(LayoutKind.Sequential)]

    private struct LogicalProcessorInfoHeader

    {

        public int Relationship;

        public uint Size;

    }



    [DllImport("kernel32.dll")]

    private static extern IntPtr GetCurrentThread();



    [DllImport("kernel32.dll")]

    private static extern IntPtr GetCurrentProcess();



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool SetThreadGroupAffinity(

        IntPtr hThread, ref GroupAffinity groupAffinity, out GroupAffinity previousGroupAffinity);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool SetThreadSelectedCpuSets(

        IntPtr Thread, uint[] CpuSetIds, uint CpuSetIdCount);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool GetSystemCpuSetInformation(

        IntPtr Information, uint BufferLength, ref uint ReturnedLength, IntPtr Process, uint Flags);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool GetLogicalProcessorInformationEx(

        int relationshipType, IntPtr buffer, ref uint returnedLength);



    [DllImport("libc", SetLastError = true)]

    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, byte[] mask);



    internal readonly record struct CpuSnapshot(

        int PerformanceCoreCount,

        int EfficientCoreCount,

        int LogicalProcessorCount,

        bool IsHybrid);

}


