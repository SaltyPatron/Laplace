using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static partial class CpuTopology
{
    private static CpuSnapshot DetectPlatform()

    {

        var pools = Pools;

        if (pools.IsHybrid)

            return new CpuSnapshot(pools.PhysicalPCores, pools.PhysicalECores, pools.LogicalCount, true);



        // Non-hybrid: the detected physical core count, not ProcessorCount (which counts
        // SMT threads), sizes every pool derived from PerformanceCoreCount. Uniform pools set
        // PhysicalPCores to the logical count. The clamp reads the pools' logical count rather
        // than the ambient machine so a synthetic pools override is honored.
        int logical = pools.LogicalCount > 0
            ? pools.LogicalCount
            : Math.Max(1, Environment.ProcessorCount);

        int physical = pools.PhysicalPCores > 0 ? Math.Min(pools.PhysicalPCores, logical) : logical;

        return new CpuSnapshot(physical, 0, logical, IsHybrid: false);

    }







    private const uint CpuSetInformationType = 0;



    private static bool TryDetectWindowsCpuSetPools([NotNullWhen(true)] out TopologyPools? pools)

    {

        pools = null;

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



    private static bool TryBuildPoolsFromCpuSets(List<CpuSetEntry> entries, string source, [NotNullWhen(true)] out TopologyPools? pools)

    {

        pools = null;

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







    private static bool TryDetectWindowsGlpiexPools([NotNullWhen(true)] out TopologyPools? pools)

    {

        pools = null;

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


}
