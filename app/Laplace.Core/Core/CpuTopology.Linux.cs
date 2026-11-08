using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static partial class CpuTopology
{
    private static bool TryDetectLinuxSysfsPools([NotNullWhen(true)] out TopologyPools? pools)

    {

        pools = null;

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



    /// <summary>
    /// Physical-core detection for NON-HYBRID Linux, from the generic per-CPU topology every
    /// Linux CPU exposes. Covers plain SMT x86, AMD multi-die, ARM server parts with no SMT,
    /// and dual-socket hosts -- none of which publish /sys/devices/cpu_core/cpus.
    ///
    /// The CPU set comes from /proc/self/status Cpus_allowed_list, NOT from
    /// /sys/devices/system/cpu/present. sysfs describes the HOST; a container under a cpuset
    /// still sees every host CPU there, so `present` would size a 2-CPU container's pools for
    /// a 64-core machine. Cpus_allowed_list is what this process may actually run on.
    ///
    /// The result is additionally clamped to Environment.ProcessorCount, which on .NET
    /// honours a cgroup CPU QUOTA (cpu.max) -- a quota restricts how much CPU time the
    /// process gets without restricting WHICH cpus it may touch, so it is invisible to the
    /// affinity mask and has to be applied separately.
    /// </summary>
    internal static bool TryDetectLinuxGenericSysfsPools([NotNullWhen(true)] out TopologyPools? pools)
    {
        pools = null;
        try
        {
            int[] all = ReadLinuxAllowedCpus();
            if (all.Length == 0) return false;

            var primaries = DedupeLinuxPrimaryCores(all);
            if (primaries.Length == 0) return false;

            int quota = Math.Max(1, Environment.ProcessorCount);
            if (primaries.Length > quota) primaries = primaries.Take(quota).ToArray();

            pools = new TopologyPools(
                isHybrid: false,
                physicalPCores: primaries.Length,
                physicalECores: 0,
                logicalCount: quota,
                primaryPLogicalCount: Math.Min(all.Length, quota),
                primaryPCoreGlobalIndices: primaries,
                primaryPCoreCpuSetIds: Array.Empty<uint>(),
                primaryPCoreAffinities: primaries
                    .Select(i => new ProcessorAffinity((ushort)(i / 64), 1UL << (i % 64))).ToArray(),
                efficientCoreGlobalIndices: Array.Empty<int>(),
                efficientCoreCpuSetIds: Array.Empty<uint>(),
                efficientCoreAffinities: Array.Empty<ProcessorAffinity>(),
                source: "linux-sysfs-generic");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "cpu_topology: generic Linux sysfs detection FAILED — worker pools will be "
                + $"mis-sized. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// The CPUs this process may run on, honouring cpuset/taskset. Falls back to the host's
    /// present list only when /proc is unavailable.
    internal static int[] ReadLinuxAllowedCpus()
    {
        const string statusPath = "/proc/self/status";
        if (File.Exists(statusPath))
            foreach (string line in File.ReadLines(statusPath))
                if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                {
                    var list = ParseCpuList(line["Cpus_allowed_list:".Length..].Trim());
                    if (list.Length > 0) return list;
                    break;
                }

        const string presentPath = "/sys/devices/system/cpu/present";
        return File.Exists(presentPath)
            ? ParseCpuList(File.ReadAllText(presentPath).Trim())
            : Array.Empty<int>();
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

            // Keyed on (package, core): core_id restarts per socket, so a bare
            // core_id collapses socket 1 onto socket 0 and HALVES the count on a
            // dual-socket host -- the common server shape.
            string pkgPath = $"/sys/devices/system/cpu/cpu{cpu}/topology/physical_package_id";
            int pkg = File.Exists(pkgPath)
                   && int.TryParse(File.ReadAllText(pkgPath).Trim(), out int pk) ? pk : 0;
            if (seen.Add(pkg * 65536 + coreId)) primaries.Add(cpu);

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
